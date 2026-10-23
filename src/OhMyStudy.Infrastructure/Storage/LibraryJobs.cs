using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    public Task SaveJobEventAsync(Job snapshot, JobEvent jobEvent, CancellationToken cancellationToken = default)
    {
        // Freeze mutable collection properties before enqueuing; the database owns this snapshot.
        using var snapshotDocument = JsonDocument.Parse(DomainJson.Serialize(snapshot));
        using var eventDocument = JsonDocument.Parse(DomainJson.Serialize(jobEvent));
        return SaveJobEventAsync(snapshotDocument.RootElement, eventDocument.RootElement, cancellationToken);
    }

    public Task SaveJobEventAsync(JsonElement snapshot, JsonElement jobEvent, CancellationToken cancellationToken = default)
    {
        var frozenSnapshot = snapshot.Clone(); var frozenEvent = jobEvent.Clone();
        return Run(() => Transaction(cancellationToken, () => SaveJobEvent(frozenSnapshot, frozenEvent)), cancellationToken, true);
    }

    private int SaveJobEvent(JsonElement snapshot, JsonElement jobEvent)
    {
        var snapshotJson = snapshot.GetRawText(); var eventJson = jobEvent.GetRawText();
        var id = snapshot.GetProperty("id").GetString()!; ValidId(id);
        var sequence = jobEvent.GetProperty("sequence").GetUInt64();
        if (sequence > long.MaxValue || jobEvent.GetProperty("job_id").GetString() != id || snapshot.GetProperty("sequence").GetUInt64() != sequence)
            throw new ArgumentException("The task snapshot does not match the event");
        var progress = snapshot.GetProperty("progress").GetDouble();
        if (!double.IsFinite(progress) || progress is < 0 or > 100) throw new ArgumentException("Invalid task progress");
        var eventId = jobEvent.TryGetProperty("id", out var eventIdElement) ? eventIdElement.GetString()! : NewId();
        var kind = snapshot.GetProperty("kind").GetString()!; var state = snapshot.GetProperty("state").GetString()!;
        var created = snapshot.GetProperty("created_at").GetRawText(); var updated = snapshot.GetProperty("updated_at").GetRawText();
        var message = snapshot.GetProperty("message").GetString()!;
        var eventCreated = jobEvent.GetProperty("created_at").GetRawText(); var eventKind = jobEvent.GetProperty("kind").GetString()!;
        var previous = Scalar("SELECT MAX(sequence) FROM job_events WHERE job_id=$p0", id);
        if (previous is not null && (long)sequence <= Convert.ToInt64(previous)) throw new InvalidOperationException("Task event sequence numbers must increase");
        Execute("INSERT INTO jobs(id,kind,state,created_at,updated_at,progress,message,snapshot_json) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6,$p7) ON CONFLICT(id) DO UPDATE SET state=excluded.state,updated_at=excluded.updated_at,progress=excluded.progress,message=excluded.message,snapshot_json=excluded.snapshot_json", id, kind, state, created, updated, progress / 100, message, snapshotJson);
        Execute("INSERT INTO job_events(id,job_id,sequence,created_at,kind,payload_json) VALUES($p0,$p1,$p2,$p3,$p4,$p5)", eventId, id, (long)sequence, eventCreated, eventKind, eventJson);
        return 0;
    }

    public Task<List<Job>> LoadJobSnapshotsAsync(CancellationToken cancellationToken = default) => Run(() =>
        Query("SELECT snapshot_json FROM jobs ORDER BY created_at,id", r => DomainJson.Deserialize<Job>(r.GetString(0))), cancellationToken);

    public Task<List<JobEvent>> ListJobEventsAsync(string jobId, CancellationToken cancellationToken = default)
    {
        ValidId(jobId);
        return Run(() => Query("SELECT payload_json FROM job_events WHERE job_id=$p0 ORDER BY sequence", r => DomainJson.Deserialize<JobEvent>(r.GetString(0)), jobId), cancellationToken);
    }

    public Task<string> VerifyIntegrityAsync(CancellationToken cancellationToken = default) => Run(() =>
    {
        VerifyDatabase(connection);
        if (Writable && Convert.ToString(Scalar("PRAGMA journal_mode")) != "wal") throw new InvalidDataException("Invalid library journal mode");
        return "SQLite integrity_check=ok; foreign_keys=on; foreign_key_check=ok";
    }, cancellationToken);

    private static void VerifyDatabase(Microsoft.Data.Sqlite.SqliteConnection database)
    {
        using var command = database.CreateCommand(); command.CommandText = "PRAGMA integrity_check";
        if (Convert.ToString(command.ExecuteScalar()) != "ok") throw new InvalidDataException("Library verification failed");
        command.CommandText = "PRAGMA foreign_key_check";
        using var reader = command.ExecuteReader();
        if (reader.Read()) throw new InvalidDataException("Library relationship verification failed");
    }

    public Task<LibraryInspection> InspectAsync(CancellationToken cancellationToken = default) => Run(() =>
    {
        var integrity = true;
        try { VerifyDatabase(connection); } catch (InvalidDataException) { integrity = false; }
        var missing = new List<string>();
        foreach (var name in Query("SELECT original_file FROM assets UNION SELECT thumbnail_file FROM assets WHERE thumbnail_file IS NOT NULL", r => r.GetString(0)))
            try { _ = SafeAssetFile(name); } catch (IOException) { missing.Add(name); }
        return new LibraryInspection(Convert.ToInt32(Scalar("PRAGMA user_version")), integrity, missing);
    }, cancellationToken);
}
