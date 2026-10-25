using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Storage;

var target = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "oh-my-study-native-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(target);
// A fresh per-run subdirectory avoids touching a prior run or a real library.
var library = Path.Combine(target, "synthetic-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(library);
var results = new List<ProbeResult>();

void Check(string name, Action action)
{
    try { action(); results.Add(new(name, true, null)); }
    catch (Exception error) { results.Add(new(name, false, error.GetType().Name + ": " + error.Message)); }
}
void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

Check("native-aot-runtime", () => Require(!RuntimeFeature.IsDynamicCodeSupported, "必须运行 Release Native AOT 发布产物"));
Check("single-writer-lease", () =>
{
    using (LibraryWriterLease.Acquire(library))
    {
        try { using var second = LibraryWriterLease.Acquire(library); throw new InvalidOperationException("重复写入锁成功"); }
        catch (IOException) { }
    }
    using var reacquired = LibraryWriterLease.Acquire(library);
});
Check("sqlite-legacy-schema-transaction-wal-backup", () =>
{
    using var database = new SqliteLibrary(LegacyLibraryLocation.DatabaseFile(library));
    foreach (var fixture in new[] { "001_initial.sql", "002_grading.sql" })
    {
        using var schema = database.Connection.CreateCommand();
        schema.CommandText = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture));
        schema.ExecuteNonQuery();
    }
    using var command = database.Connection.CreateCommand();
    command.CommandText = "PRAGMA foreign_keys";
    Require(Convert.ToInt64(command.ExecuteScalar()) == 1, "外键未启用");
    command.CommandText = "PRAGMA journal_mode";
    Require((string?)command.ExecuteScalar() == "wal", "WAL 未启用");
    var name = "合成资料 ' ; --";
    command.CommandText = "INSERT INTO documents(id,name,mode,created_at) VALUES($id,$name,$mode,'2026-10-18T00:00:00Z')";
    command.Parameters.AddWithValue("$id", "fixture-document");
    command.Parameters.AddWithValue("$name", name);
    command.Parameters.AddWithValue("$mode", "\"blank\""); // Legacy SQLite stores serde's JSON enum string, including quotes.
    command.ExecuteNonQuery();
    using (var transaction = database.Connection.BeginTransaction())
    {
        using var insert = database.Connection.CreateCommand();
        insert.Transaction = transaction;
        insert.CommandText = "INSERT INTO providers(id,config_json) VALUES('rollback-provider','{}')";
        insert.ExecuteNonQuery();
        transaction.Rollback();
    }
    command.Parameters.Clear();
    command.CommandText = "SELECT COUNT(*) FROM providers";
    Require(Convert.ToInt64(command.ExecuteScalar()) == 0, "事务失败未回滚");
    command.CommandText = "INSERT INTO source_pages(id,document_id,asset_id,page_index,preview_status,role) VALUES('orphan','missing','missing',0,'ready','material')";
    try { command.ExecuteNonQuery(); throw new InvalidOperationException("外键约束失效"); }
    catch (SqliteException e) when (e.SqliteErrorCode == 19) { }
    var backupFile = Path.Combine(library, "consistent-backup.sqlite3");
    database.BackupTo(backupFile);
    using var restored = new SqliteLibrary(backupFile);
    using var select = restored.Connection.CreateCommand();
    select.CommandText = "SELECT name FROM documents WHERE id='fixture-document'";
    Require((string?)select.ExecuteScalar() == name, "在线备份未包含已提交的 WAL 数据或字符串被改变");
    select.CommandText = "PRAGMA integrity_check";
    Require((string?)select.ExecuteScalar() == "ok", "备份完整性检查失败");
    try { database.BackupTo(backupFile); throw new InvalidOperationException("备份覆盖已有文件"); }
    catch (IOException) { }
});
Check("credential-target-validation", () =>
{
    foreach (var reference in new[] { "foreign/credential", "OhMyStudy/provider/not-a-uuid" })
    {
        try { WindowsCredentialStore.Read(reference); throw new InvalidOperationException("接受了外来凭据引用"); }
        catch (ArgumentException) { }
    }
});
if (args.Contains("--credentials", StringComparer.Ordinal))
{
    Check("windows-credential-utf8-roundtrip", () =>
    {
        var reference = WindowsCredentialStore.NewReference();
        byte[] secret = Encoding.UTF8.GetBytes("native-p1-合成凭据-only");
        byte[]? actual = null;
        try
        {
            Require(WindowsCredentialStore.Read(reference) is null, "随机合成引用已存在");
            WindowsCredentialStore.Save(reference, secret);
            actual = WindowsCredentialStore.Read(reference);
            Require(actual is not null && actual.AsSpan().SequenceEqual(secret), "旧格式 UTF-8 blob 往返不一致");
            WindowsCredentialStore.Delete(reference);
            Require(WindowsCredentialStore.Read(reference) is null, "合成凭据删除失败");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(secret);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
            WindowsCredentialStore.Delete(reference);
        }
    });
}
var report = new ProbeReport(DateTimeOffset.UtcNow.ToString("O"), RuntimeFeature.IsDynamicCodeSupported,
    Environment.OSVersion.VersionString, Environment.Version.ToString(), library, results.ToArray());
var reportFile = Path.Combine(target, "infrastructure-report.json");
File.WriteAllText(reportFile, JsonSerializer.Serialize(report, ProbeJson.Default.ProbeReport));
foreach (var result in results) Console.WriteLine($"{(result.Passed ? "PASS" : "FAIL")} {result.Name}{(result.Error is null ? "" : ": " + result.Error)}");
Console.WriteLine(reportFile);
return results.All(r => r.Passed) ? 0 : 1;

internal sealed record ProbeResult(string Name, bool Passed, string? Error);
internal sealed record ProbeReport(string TimestampUtc, bool DynamicCodeSupported, string WindowsVersion,
    string RuntimeVersion, string SyntheticLibrary, ProbeResult[] Results);

[JsonSourceGenerationOptions(WriteIndented = true, PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower)]
[JsonSerializable(typeof(ProbeReport))]
internal sealed partial class ProbeJson : JsonSerializerContext;
