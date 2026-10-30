using System.IO.Compression;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    public Task SaveGradingLayoutAsync(string jobId, ExamLayout layout, CancellationToken cancellationToken = default)
    {
        var json = DomainJson.Serialize(layout);
        return Run(() => Transaction(cancellationToken, () =>
        {
            var input = CheckRun(jobId);
            var parsed = DomainJson.Deserialize<ExamLayout>(json);
            _ = parsed.Apply(input.Document);
            return Execute("UPDATE grading_runs SET input_json=$p0 WHERE job_id=$p1", DomainJson.Serialize(input with { Layout = parsed }), jobId);
        }), cancellationToken, true);
    }

    public Task SaveWorkflowArtifactAsync(string jobId, string name, string mime, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ValidId(jobId);
        if (string.IsNullOrWhiteSpace(name) || name.Length > 256 || bytes.LongLength > 256L * 1024 * 1024)
            throw new ArgumentException("Invalid recognition artifact name or size.");
        var copy = bytes.ToArray();
        return Run(() => Transaction(cancellationToken, () =>
        {
            CheckRun(jobId);
            return Execute("INSERT INTO workflow_artifacts(job_id,name,mime,content) VALUES($p0,$p1,$p2,$p3) ON CONFLICT(job_id,name) DO UPDATE SET mime=excluded.mime,content=excluded.content",
                jobId, name, mime, copy);
        }), cancellationToken, true);
    }

    public Task<byte[]?> ReadWorkflowArtifactAsync(string jobId, string name, CancellationToken cancellationToken = default)
    {
        ValidId(jobId);
        return Run(() => Scalar("SELECT content FROM workflow_artifacts WHERE job_id=$p0 AND name=$p1", jobId, name) as byte[], cancellationToken);
    }

    public async Task ExportWorkflowArtifactsAsync(string jobId, Stream destination, CancellationToken cancellationToken = default)
    {
        ValidId(jobId);
        var names = await Run(() => Convert.ToInt32(Scalar("PRAGMA user_version")) < 7 ? []
            : Query("SELECT name FROM workflow_artifacts WHERE job_id=$p0 ORDER BY name", r => r.GetString(0), jobId), cancellationToken).ConfigureAwait(false);
        if (names.Count == 0) throw new InvalidOperationException("No recognition materials are available for this task.");
        using var zip = new ZipArchive(destination, ZipArchiveMode.Create, leaveOpen: true);
        foreach (var name in names)
        {
            if (name.Contains('\\') || name.Contains(':') || name.StartsWith('/') || name.Split('/').Any(part => part is ".." or "." or ""))
                throw new InvalidDataException("Recognition artifact paths must stay inside the export archive.");
            var bytes = await ReadWorkflowArtifactAsync(jobId, name, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("The recognition materials changed during export.");
            await using var stream = zip.CreateEntry(name, CompressionLevel.Fastest).Open();
            await stream.WriteAsync(bytes, cancellationToken).ConfigureAwait(false);
        }
    }
}
