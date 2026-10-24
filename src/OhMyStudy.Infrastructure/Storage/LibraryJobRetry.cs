using System.Text.Json;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    private void TrackGradingChange(string jobId, string kind, string key, string? before, string? after) =>
        Execute("INSERT INTO grading_rollback(job_id,kind,item_key,before_json,after_json) VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(job_id,kind,item_key) DO UPDATE SET after_json=excluded.after_json", jobId, kind, key, before, after);

    private string? PaperInfoJson(string documentId) => Scalar("SELECT json_object('subject',subject,'earned',earned,'possible',possible,'manual',manual) FROM paper_info WHERE document_id=$p0", documentId) as string;

    public Task RetryJobAsync(Job previous, Job replacement, JobEvent jobEvent, CancellationToken cancellationToken = default)
    {
        ValidId(previous.Id); ValidId(replacement.Id);
        var snapshot = DomainJson.ToElement(replacement); var nextEvent = DomainJson.ToElement(jobEvent);
        return Run(() =>
        {
            var removedFiles = new List<string>();
            Transaction(cancellationToken, () =>
            {
                var current = Scalar("SELECT snapshot_json FROM jobs WHERE id=$p0", previous.Id) as string
                    ?? throw new KeyNotFoundException("Task not found.");
                var stored = DomainJson.Deserialize<Job>(current);
                if (stored.State is not (JobState.Failed or JobState.Interrupted) || stored.Sequence != previous.Sequence)
                    throw new InvalidOperationException("Only failed or interrupted tasks can be retried.");
                if (replacement.State != JobState.Queued || replacement.Id == previous.Id || replacement.Kind != stored.Kind
                    || replacement.Input?.Document.Id != stored.Input?.Document.Id)
                    throw new ArgumentException("The replacement task does not match the failed task.");
                if (stored.Input is { } input)
                {
                    DocumentIdle(input.Document.Id);
                    _ = Document(input.Document.Id);
                    if (Scalar("SELECT 1 FROM grading_runs WHERE document_id=$p0 AND rowid>(SELECT rowid FROM grading_runs WHERE job_id=$p1)", input.Document.Id, stored.Id) is not null)
                        throw new InvalidOperationException("A newer task has processed this exam. Retry its latest failed task instead.");
                    bool began = Scalar("SELECT 1 FROM grading_runs WHERE job_id=$p0", stored.Id) is not null;
                    if (began)
                    {
                        if (Scalar("SELECT name FROM sqlite_master WHERE type='table' AND name='grading_rollback'") is null
                            || Scalar("SELECT 1 FROM grading_rollback WHERE job_id=$p0 AND kind='run'", stored.Id) is null)
                            throw new InvalidOperationException("This task predates retry recovery records. Use Recognize again in Exams to preserve its saved results.");
                        RollbackGrading(stored.Id, input.Document.Id, removedFiles);
                    }
                }
                Execute("DELETE FROM model_calls WHERE job_id=$p0", stored.Id);
                Execute("DELETE FROM grading_runs WHERE job_id=$p0", stored.Id);
                Execute("DELETE FROM jobs WHERE id=$p0", stored.Id);
                return SaveJobEvent(snapshot, nextEvent);
            });
            foreach (string file in removedFiles)
            {
                try { if (File.Exists(file)) { RejectLinks(file); File.Delete(file); } }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    OperationDiagnostics.Record("Task.Retry.Cleanup", new() { ["job_id"] = previous.Id, ["path"] = file, ["exception"] = error.ToString() }, failed: true);
                }
            }
            return 0;
        }, cancellationToken, true);
    }

    private void RollbackGrading(string jobId, string documentId, List<string> removedFiles)
    {
        var changes = Query("SELECT kind,item_key,before_json,after_json FROM grading_rollback WHERE job_id=$p0", r =>
            (Kind: r.GetString(0), Key: r.GetString(1), Before: NullableText(r, 2), After: NullableText(r, 3)), jobId);
        foreach (var change in changes.Where(c => c.Kind == "question"))
        {
            var current = Questions(documentId).SingleOrDefault(q => q.Id == change.Key);
            if (current is null) continue;
            var written = DomainJson.Deserialize<SavedQuestion>(change.After!);
            if (!JsonEqual(DomainJson.Serialize(current.Question with { Notes = "" }), DomainJson.Serialize(written.Question with { Notes = "" })))
                throw new InvalidOperationException("Questions saved by this task were edited. Use Recognize again in Exams to preserve your edits.");
            if (change.Before is null)
            {
                if (current.Question.Notes.Length > 0)
                    throw new InvalidOperationException("Questions saved by this task were edited. Use Recognize again in Exams to preserve your edits.");
                Execute("DELETE FROM questions WHERE id=$p0", current.Id);
            }
            else
            {
                var before = DomainJson.Deserialize<SavedQuestion>(change.Before);
                WriteQuestion(documentId, before.Question with { Notes = current.Question.Notes }, before.Id);
                Execute("UPDATE questions SET updated_at=$p0 WHERE id=$p1", before.UpdatedAt, before.Id);
                if (Scalar("SELECT name FROM sqlite_master WHERE type='table' AND name='question_embeddings'") is not null)
                    Execute("DELETE FROM question_embeddings WHERE question_id=$p0", before.Id);
            }
        }
        foreach (var change in changes.Where(c => c.Kind == "paper"))
        {
            if (PaperInfoJson(documentId) != change.After) continue;
            Execute("DELETE FROM paper_info WHERE document_id=$p0", documentId);
            if (change.Before is not null)
            {
                using var json = JsonDocument.Parse(change.Before);
                var row = json.RootElement;
                WritePaperInfo(documentId, row.GetProperty("subject").GetString(), Number(row, "earned"), Number(row, "possible"), row.GetProperty("manual").GetInt32() != 0);
            }
        }
        foreach (var change in changes.Where(c => c.Kind == "title"))
            if (DomainJson.Serialize(Document(documentId).Name) == change.After && change.Before is not null)
                Execute("UPDATE documents SET name=$p0 WHERE id=$p1", DomainJson.Deserialize<string>(change.Before), documentId);
        var used = Questions(documentId).SelectMany(q => q.Question.Stem.Concat(q.Question.Items.SelectMany(i => i.Prompt)))
            .OfType<ImageContent>().Select(i => i.AssetId).OfType<string>().ToHashSet(StringComparer.Ordinal);
        foreach (var change in changes.Where(c => c.Kind == "asset" && !used.Contains(c.Key)))
        {
            var files = Query("SELECT original_file,thumbnail_file FROM assets WHERE id=$p0", r => (Original: r.GetString(0), Thumbnail: NullableText(r, 1)), change.Key);
            foreach (var file in files.SelectMany(f => new[] { f.Original, f.Thumbnail }).OfType<string>())
            {
                ValidateFileName(file);
                string path = Path.GetFullPath(Path.Combine(AssetRoot, file));
                if (!path.StartsWith(Path.TrimEndingDirectorySeparator(AssetRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("The generated image is outside the asset directory.");
                removedFiles.Add(path);
            }
            Execute("DELETE FROM question_assets WHERE asset_id=$p0", change.Key);
            Execute("DELETE FROM assets WHERE id=$p0", change.Key);
        }
    }

    private static double? Number(JsonElement row, string name) => row.GetProperty(name) is { ValueKind: JsonValueKind.Number } number ? number.GetDouble() : null;
}
