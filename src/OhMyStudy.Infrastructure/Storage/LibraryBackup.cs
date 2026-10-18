using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    private void BackupDatabase(string destination)
    {
        using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            backup.Open(); connection.BackupDatabase(backup); VerifyDatabase(backup);
        }
        catch { File.Delete(destination); throw; }
    }

    /// <summary>Creates a new directory containing the online SQLite snapshot and every managed file.
    /// Recovery never replaces an open/original library, and credentials are never exported.</summary>
    public Task<BackupResult> BackupAsync(string destinationDirectory, CancellationToken cancellationToken = default)
    {
        var destination = Path.GetFullPath(destinationDirectory);
        if (destination.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || destination.Equals(Root, StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份目录需位于资料库之外");
        return Run(() =>
        {
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("备份位置已存在，请选择新目录");
            var parent = Path.GetDirectoryName(destination)!; RejectLinks(parent);
            var stage = Path.Combine(parent, ".study-backup-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(stage);
            try
            {
                // The native lease excludes cooperating clients; BEGIN IMMEDIATE also freezes SQLite
                // writers while the file inventory is taken. Do not run BackupDatabase inside this
                // transaction on its source connection: SQLite backup would busy-loop on itself.
                if (Writable) Execute("BEGIN IMMEDIATE");
                else
                {
                    RejectLegacyProcesses();
                    Execute("BEGIN");
                    _ = Scalar("SELECT COUNT(*) FROM documents"); // Establish the read snapshot before online backup.
                }
                var copied = new List<(string Name, string Sha256)>();
                try
                {
                    using (var target = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(stage, "study.sqlite3"), Pooling = false }.ToString()))
                    {
                        target.Open();
                        if (Writable)
                        {
                            using var source = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = connection.DataSource, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
                            source.Open(); source.BackupDatabase(target);
                        }
                        else connection.BackupDatabase(target);
                        VerifyDatabase(target);
                    }
                    Directory.CreateDirectory(Path.Combine(stage, "assets"));
                    foreach (var name in Query("SELECT original_file FROM assets UNION SELECT thumbnail_file FROM assets WHERE thumbnail_file IS NOT NULL", r => r.GetString(0)))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var path = SafeAssetFile(name);
                        var output = Path.Combine(stage, "assets", name);
                        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                        if (input.Length is < 1 or > LibraryLimits.MaximumAssetBytes) throw new InvalidDataException("备份原件大小无效");
                        using (var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { input.CopyTo(file); file.Flush(true); }
                        copied.Add((name, HashFile(output)));
                    }
                    cancellationToken.ThrowIfCancellationRequested();
                    Execute("COMMIT");
                }
                catch { Execute("ROLLBACK"); throw; }
                using (var manifest = new FileStream(Path.Combine(stage, "backup.json"), FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    using (var json = new Utf8JsonWriter(manifest))
                    {
                        json.WriteStartObject(); json.WriteNumber("format", 1); json.WriteString("database_sha256", HashFile(Path.Combine(stage, "study.sqlite3")));
                        json.WriteStartArray("files");
                        foreach (var item in copied) { json.WriteStartObject(); json.WriteString("name", item.Name); json.WriteString("sha256", item.Sha256); json.WriteEndObject(); }
                        json.WriteEndArray(); json.WriteEndObject(); json.Flush();
                    }
                    manifest.Flush(true);
                    if (manifest.Length > 8 * 1024 * 1024) throw new InvalidOperationException("备份清单超过当前恢复支持的大小");
                }
                cancellationToken.ThrowIfCancellationRequested();
                Directory.Move(stage, destination);
                return new BackupResult(destination, copied.Count);
            }
            catch { DeleteOwnedDirectory(stage); throw; }
        }, cancellationToken);
    }

    public static async Task RestoreBackupAsync(string backupDirectory, string newLibraryDirectory, CancellationToken cancellationToken = default)
    {
        await Task.Run(() =>
        {
            SqliteLibrary.Initialize();
            var backup = Path.GetFullPath(backupDirectory); var destination = Path.GetFullPath(newLibraryDirectory);
            RejectLinks(backup);
            if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("恢复位置已存在，请选择新目录");
            var parent = Path.GetDirectoryName(destination)!; RejectLinks(parent);
            var manifestPath = Path.Combine(backup, "backup.json"); RejectLinks(manifestPath);
            if (new FileInfo(manifestPath).Length > 8 * 1024 * 1024) throw new InvalidDataException("备份清单过大");
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(manifestPath));
            var root = manifest.RootElement;
            if (root.GetProperty("format").GetInt32() != 1) throw new InvalidDataException("备份格式不受支持");
            var databasePath = Path.Combine(backup, "study.sqlite3"); RejectLinks(databasePath);
            if (!HashFile(databasePath).Equals(root.GetProperty("database_sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份资料库校验失败");
            var stage = Path.Combine(parent, ".study-restore-" + Guid.NewGuid().ToString("N")); Directory.CreateDirectory(stage);
            try
            {
                Directory.CreateDirectory(Path.Combine(stage, "assets"));
                File.Copy(databasePath, Path.Combine(stage, "study.sqlite3"), false);
                if (!HashFile(Path.Combine(stage, "study.sqlite3")).Equals(root.GetProperty("database_sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("复制后的资料库校验失败");
                var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var item in root.GetProperty("files").EnumerateArray())
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var name = item.GetProperty("name").GetString()!; ValidateFileName(name);
                    if (!names.Add(name)) throw new InvalidDataException("备份清单重复");
                    var source = Path.Combine(backup, "assets", name); RejectLinks(source);
                    if (new FileInfo(source).Length is < 1 or > LibraryLimits.MaximumAssetBytes) throw new InvalidDataException("备份原件大小无效");
                    var output = Path.Combine(stage, "assets", name); File.Copy(source, output, false);
                    if (!HashFile(output).Equals(item.GetProperty("sha256").GetString(), StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("备份原件校验失败");
                }
                using (var database = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(stage, "study.sqlite3"), Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString()))
                {
                    database.Open(); VerifyDatabase(database);
                    using var command = database.CreateCommand(); command.CommandText = "SELECT original_file FROM assets UNION SELECT thumbnail_file FROM assets WHERE thumbnail_file IS NOT NULL";
                    using var reader = command.ExecuteReader();
                    while (reader.Read()) if (!names.Contains(reader.GetString(0))) throw new InvalidDataException("备份缺少原件");
                }
                cancellationToken.ThrowIfCancellationRequested(); Directory.Move(stage, destination);
            }
            catch { DeleteOwnedDirectory(stage); throw; }
        }, cancellationToken).ConfigureAwait(false);
    }

    private static string HashFile(string path)
    { using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read); return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant(); }

    private static void DeleteOwnedDirectory(string path)
    {
        // This path is an unguessable staging directory created by this operation. Still reject
        // links before cleanup so a filesystem substitution cannot redirect recursive deletion.
        if (!Directory.Exists(path)) return;
        RejectLinks(path);
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories)) { RejectLinks(file); File.Delete(file); }
        foreach (var directory in Directory.EnumerateDirectories(path).OrderByDescending(p => p.Length)) { RejectLinks(directory); Directory.Delete(directory); }
        Directory.Delete(path);
    }
}
