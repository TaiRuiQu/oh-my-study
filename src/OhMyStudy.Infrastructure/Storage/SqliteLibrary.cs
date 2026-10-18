using Microsoft.Data.Sqlite;

namespace OhMyStudy.Infrastructure.Storage;

/// <summary>P1 native engine and backup boundary. Does not implement the P3 repositories.</summary>
public sealed class SqliteLibrary : IDisposable
{
    private static readonly object InitializationLock = new();
    private static bool initialized;
    public SqliteConnection Connection { get; }

    public SqliteLibrary(string databaseFile)
    {
        Initialize();
        Connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.GetFullPath(databaseFile),
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
            DefaultTimeout = 5
        }.ToString());
        try
        {
            Connection.Open();
            using var command = Connection.CreateCommand();
            command.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;";
            command.ExecuteNonQuery();
        }
        catch { Connection.Dispose(); throw; }
    }

    /// <summary>SQLite's online backup includes committed WAL data. Never copy only the main file.</summary>
    public void BackupTo(string destinationFile)
    {
        var destination = Path.GetFullPath(destinationFile);
        if (string.Equals(destination, Path.GetFullPath(Connection.DataSource), StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份文件不能覆盖源资料库");
        // Reserve a unique destination so an existing user's backup is never overwritten.
        using (new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        try
        {
            using var backup = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = destination, Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString());
            backup.Open();
            Connection.BackupDatabase(backup);
        }
        catch
        {
            File.Delete(destination);
            throw;
        }
    }

    public void Dispose() => Connection.Dispose();

    internal static void Initialize()
    {
        lock (InitializationLock)
            if (!initialized) { SQLitePCL.Batteries_V2.Init(); initialized = true; }
    }
}
