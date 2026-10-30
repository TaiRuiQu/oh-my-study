using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

/// <summary>A single owner thread executes all SQLite work and commits in enqueue order.</summary>
public sealed partial class LibraryRepository : IDisposable, IAsyncDisposable
{
    private readonly BlockingCollection<Action> work = new(new ConcurrentQueue<Action>(), 256);
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource stopped = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Thread thread;
    private readonly LibraryOpenOptions options;
    private readonly SemaphoreSlim mediaGate = new(1, 1);
    private SqliteConnection connection = null!;
    private LibraryWriterLease? lease;
    private int disposed;
    private string Root => Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.Directory));
    private string AssetRoot => Path.Combine(Root, "assets");
    private bool Writable => options.Mode != LibraryOpenMode.ReadOnly;
    public bool IsReadOnly => !Writable;

    private LibraryRepository(LibraryOpenOptions options)
    {
        this.options = options;
        thread = new Thread(Worker) { Name = "study-database", IsBackground = true };
        thread.Start();
    }

    public static async Task<LibraryRepository> OpenAsync(LibraryOpenOptions options, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var repository = new LibraryRepository(options);
        try { await repository.ready.Task.WaitAsync(cancellationToken).ConfigureAwait(false); return repository; }
        catch { await repository.DisposeAsync().ConfigureAwait(false); throw; }
    }

    private void Worker()
    {
        try
        {
            Open();
            ready.TrySetResult();
            foreach (var action in work.GetConsumingEnumerable()) action();
        }
        catch (Exception error) { ready.TrySetException(error); }
        finally { connection?.Dispose(); lease?.Dispose(); stopped.TrySetResult(); }
    }

    private void Open()
    {
        SqliteLibrary.Initialize();
        var dbFile = LegacyLibraryLocation.DatabaseFile(Root);
        if (options.Mode == LibraryOpenMode.CreateNew)
        {
            if (File.Exists(dbFile)) throw new IOException("The library already exists. Choose how to open it");
            var existing = Root;
            while (!Directory.Exists(existing)) existing = Path.GetDirectoryName(existing) ?? throw new DirectoryNotFoundException("Invalid library directory");
            RejectLinks(existing);
            Directory.CreateDirectory(Root);
        }
        if (!Directory.Exists(Root) || !File.Exists(dbFile) && options.Mode != LibraryOpenMode.CreateNew)
            throw new DirectoryNotFoundException("Library not found");
        RejectLinks(Root);
        if (File.Exists(dbFile)) RejectLinks(dbFile);
        if (Writable)
        {
            RejectLegacyProcesses();
            lease = LibraryWriterLease.Acquire(Root);
            if (options.Mode == LibraryOpenMode.CreateNew)
                using (new FileStream(dbFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
        }
        connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbFile,
            Pooling = false,
            DefaultTimeout = 5,
            Mode = options.Mode switch { LibraryOpenMode.ReadOnly => SqliteOpenMode.ReadOnly, LibraryOpenMode.CreateNew => SqliteOpenMode.ReadWriteCreate, _ => SqliteOpenMode.ReadWrite }
        }.ToString());
        connection.Open();
        Execute("PRAGMA foreign_keys=ON; PRAGMA busy_timeout=5000;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        if (version is < 0 or > 7) throw new InvalidDataException("Unsupported library version");
        ValidateSchema(version);
        if (Writable)
        {
            Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL;");
            if (version < 7)
            {
                if (version != 0) BackupDatabase(dbFile + ".pre-v7-" + Guid.NewGuid().ToString("N"));
                Transaction(CancellationToken.None, () =>
                {
                    if (version == 0) Execute(Migration("001_initial.sql"));
                    if (version < 2) Execute(Migration("002_grading.sql"));
                    if (version < 3) Execute(Migration("003_paper_info.sql"));
                    if (version < 4)
                    {
                        Execute(Migration("004_question_items.sql"));
                        UpgradeQuestionRecords();
                    }
                    if (version < 5) Execute(Migration("005_question_assets.sql"));
                    if (version < 6) Execute(Migration("006_student_answers.sql"));
                    Execute(Migration("007_workflow_artifacts.sql"));
                    Execute("PRAGMA user_version=7"); return 0;
                });
            }
            Directory.CreateDirectory(AssetRoot);
            RejectLinks(AssetRoot);
        }
        else if (version == 0) throw new InvalidDataException("The library has not been initialized");
    }

    private static readonly string[] QuestionSummaryColumns = ["number", "subject", "question_type", "preview", "search_text", "item_count", "verdict", "earned", "possible", "mistake", "review"];

    private static string Migration(string name)
    {
        var assembly = typeof(LibraryRepository).Assembly;
        var resource = assembly.GetManifestResourceNames().Single(n => n.EndsWith(".Storage.Migrations." + name, StringComparison.Ordinal));
        using var stream = assembly.GetManifestResourceStream(resource)!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private void ValidateSchema(int version)
    {
        if (version == 0)
        {
            if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%'")) != 0)
                throw new InvalidDataException("The unversioned library contains unknown tables. Automatic upgrade refused");
            return;
        }
        foreach (var table in new[] { "providers", "assets", "documents", "source_pages", "jobs", "job_events", "settings" })
            if (Scalar("SELECT name FROM sqlite_master WHERE type='table' AND name=$p0", table) is null) throw new InvalidDataException("The library schema is incomplete");
        if (version >= 2)
            foreach (var table in new[] { "questions", "grading_runs", "model_calls" })
                if (Scalar("SELECT name FROM sqlite_master WHERE type='table' AND name=$p0", table) is null) throw new InvalidDataException("The library schema is incomplete");
        var expected = new Dictionary<string, string[]>
        {
            ["providers"] = ["id", "config_json"],
            ["assets"] = ["id", "mime", "byte_size", "width", "height", "original_file", "thumbnail_file"],
            ["documents"] = ["id", "name", "mode", "created_at", "pending_pdf_pages"],
            ["source_pages"] = ["id", "document_id", "asset_id", "page_index", "rotation", "preview_status", "role"],
            ["jobs"] = ["id", "kind", "state", "created_at", "updated_at", "progress", "message", "snapshot_json"],
            ["job_events"] = ["id", "job_id", "sequence", "created_at", "kind", "payload_json"],
            ["settings"] = ["id", "settings_json"]
        };
        if (version >= 2)
        {
            expected["source_pages"] = [.. expected["source_pages"], "original_asset_id", "pdf_page_index"];
            expected["questions"] = ["id", "document_id", "draft_key", "content_json", "updated_at"];
            expected["grading_runs"] = ["job_id", "document_id", "input_json", "coverage_json", "references_json"];
            expected["model_calls"] = ["job_id", "ordinal", "details_json"];
        }
        if (version >= 3) expected["paper_info"] = ["document_id", "subject", "earned", "possible", "manual"];
        if (version >= 4) expected["questions"] = [.. expected["questions"], .. QuestionSummaryColumns];
        if (version >= 5) expected["question_assets"] = ["asset_id", "document_id"];
        if (version >= 7) expected["workflow_artifacts"] = ["job_id", "name", "mime", "content"];
        foreach (var (table, columns) in expected)
        {
            var actual = Query("SELECT name FROM pragma_table_info($p0)", r => r.GetString(0), table).ToHashSet(StringComparer.Ordinal);
            if (columns.Any(c => !actual.Contains(c))) throw new InvalidDataException("Library fields are incomplete");
        }
    }

    // The Rust application does not participate in our file lease. This conservative check is
    // an additional barrier, not a guarantee against another process starting afterwards.
    private static void RejectLegacyProcesses()
    {
        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                if (process.Id == Environment.ProcessId) continue;
                var name = process.ProcessName;
                if (name.Equals("oh-my-study", StringComparison.OrdinalIgnoreCase) || name.Equals("oh_my_study", StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Exit the older app before opening the library for editing");
            }
        }
    }

    private Task<T> Run<T>(Func<T> action, CancellationToken cancellationToken = default, bool write = false)
    {
        if (Volatile.Read(ref disposed) != 0) return Task.FromException<T>(new ObjectDisposedException(nameof(LibraryRepository)));
        if (write && !Writable) return Task.FromException<T>(new InvalidOperationException("The library is open read-only"));
        var result = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!work.TryAdd(() =>
            {
                try { cancellationToken.ThrowIfCancellationRequested(); result.TrySetResult(action()); }
                catch (OperationCanceledException) { result.TrySetCanceled(cancellationToken); }
                catch (Exception error) { result.TrySetException(error); }
            })) throw new InvalidOperationException("The save queue is full. Retry later");
        }
        catch (OperationCanceledException) { result.TrySetCanceled(cancellationToken); }
        catch (Exception error) { result.TrySetException(error); }
        return result.Task;
    }

    private T Transaction<T>(CancellationToken cancellationToken, Func<T> action)
    {
        Execute("BEGIN IMMEDIATE");
        try
        {
            var result = action();
            cancellationToken.ThrowIfCancellationRequested();
            Execute("COMMIT"); // Once committed, return success even if cancellation races afterwards.
            return result;
        }
        catch { Execute("ROLLBACK"); throw; }
    }

    private SqliteCommand Command(string sql, params object?[] values)
    {
        var command = connection.CreateCommand(); command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$p" + index, values[index] ?? DBNull.Value);
        return command;
    }
    private int Execute(string sql, params object?[] values) { using var command = Command(sql, values); return command.ExecuteNonQuery(); }
    private object? Scalar(string sql, params object?[] values) { using var command = Command(sql, values); var value = command.ExecuteScalar(); return value is DBNull ? null : value; }
    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> map, params object?[] values)
    {
        using var command = Command(sql, values); using var reader = command.ExecuteReader();
        var results = new List<T>(); while (reader.Read()) results.Add(map(reader)); return results;
    }
    private static string? NullableText(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static uint? NullableUInt(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : checked((uint)reader.GetInt64(index));
    private static string NewId() => Guid.NewGuid().ToString("D");
    private static string Now() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd'T'HH:mm:ss.fffffffzzz", System.Globalization.CultureInfo.InvariantCulture);
    private static void ValidId(string id) { if (!Guid.TryParse(id, out _)) throw new ArgumentException("Invalid ID"); }
    internal static void RejectLinks(string path)
    {
        for (FileSystemInfo? item = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path); item is not null; item = item is FileInfo file ? file.Directory : ((DirectoryInfo)item).Parent)
            if ((item.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("Library paths must not contain links or reparse points");
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) work.CompleteAdding();
        if (Thread.CurrentThread != thread) stopped.Task.GetAwaiter().GetResult();
    }
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) work.CompleteAdding();
        await stopped.Task.ConfigureAwait(false);
    }
}
