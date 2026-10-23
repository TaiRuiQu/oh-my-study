using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    // List rows do not load source pages, images, or question bodies.
    public Task<List<PaperSummary>> ListPapersAsync(CancellationToken ct = default) => Run(() =>
        Query("SELECT d.id,d.name,d.mode,d.created_at,d.pending_pdf_pages,i.subject,i.earned,i.possible,(SELECT COUNT(*) FROM questions q WHERE q.document_id=d.id) FROM documents d LEFT JOIN paper_info i ON i.document_id=d.id ORDER BY d.created_at DESC,d.id", r => new PaperSummary(new Document
        { Id = r.GetString(0), Name = r.GetString(1), Mode = DomainJson.Deserialize<ImportMode>(r.GetString(2)), CreatedAt = r.GetString(3), PendingPdfPages = r.GetBoolean(4), Pages = [] },
        NullableText(r, 5), r.IsDBNull(6) ? null : r.GetDouble(6), r.IsDBNull(7) ? null : r.GetDouble(7), r.GetInt32(8))), ct);

    public Task UpdatePaperAsync(string id, string name, string? subject, double? earned, double? possible, CancellationToken ct = default)
    {
        ValidId(id); ValidatePaperInfo(subject, earned, possible);
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 240) throw new ArgumentException("Titles must be between 1 and 240 characters.");
        return Run(() => Transaction(ct, () =>
        {
            DocumentIdle(id);
            if (Execute("UPDATE documents SET name=$p0 WHERE id=$p1", name.Trim(), id) != 1) throw new KeyNotFoundException();
            WritePaperInfo(id, subject, earned, possible, true); return 0;
        }), ct, true);
    }

    public Task DeletePaperAsync(string id, CancellationToken ct = default)
    {
        ValidId(id);
        return Run(() =>
        {
            var removedFiles = new List<string>();
            Transaction(ct, () =>
            {
                DocumentIdle(id);
                if (Scalar("SELECT 1 FROM documents WHERE id=$p0", id) is null) throw new KeyNotFoundException("Exam not found");
                var jobs = Query("SELECT job_id FROM grading_runs WHERE document_id=$p0", r => r.GetString(0), id);
                var assets = Query("SELECT a.id,a.original_file,a.thumbnail_file FROM assets a WHERE a.id IN (SELECT asset_id FROM source_pages WHERE document_id=$p0 UNION SELECT original_asset_id FROM source_pages WHERE document_id=$p0 AND original_asset_id IS NOT NULL UNION SELECT asset_id FROM question_assets WHERE document_id=$p0)",
                    r => (Id: r.GetString(0), Original: r.GetString(1), Thumbnail: NullableText(r, 2)), id);
                foreach (var job in jobs) Execute("DELETE FROM model_calls WHERE job_id=$p0", job);
                Execute("DELETE FROM grading_runs WHERE document_id=$p0", id);
                foreach (var job in jobs) Execute("DELETE FROM jobs WHERE id=$p0", job);
                Execute("DELETE FROM questions WHERE document_id=$p0", id);
                Execute("DELETE FROM source_pages WHERE document_id=$p0", id);
                Execute("DELETE FROM question_assets WHERE document_id=$p0", id);
                Execute("DELETE FROM paper_info WHERE document_id=$p0", id);
                if (Execute("DELETE FROM documents WHERE id=$p0", id) != 1) throw new KeyNotFoundException("Exam not found");
                foreach (var asset in assets)
                {
                    if (Convert.ToInt64(Scalar("SELECT COUNT(*) FROM source_pages WHERE asset_id=$p0 OR original_asset_id=$p0", asset.Id)) > 0) continue;
                    Execute("DELETE FROM assets WHERE id=$p0", asset.Id);
                    removedFiles.Add(asset.Original);
                    if (asset.Thumbnail is not null) removedFiles.Add(asset.Thumbnail);
                }
                return 0;
            });
            // The database commit owns the deletion result. Files are removed afterwards; a failure
            // leaves an orphan that RecoverOrphanedAssetsAsync can report and remove explicitly.
            foreach (var name in removedFiles)
            {
                try
                {
                    ValidateFileName(name); RejectLinks(AssetRoot);
                    var path = Path.GetFullPath(Path.Combine(AssetRoot, name));
                    if (!path.StartsWith(Path.TrimEndingDirectorySeparator(AssetRoot) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!File.Exists(path)) continue;
                    RejectLinks(path); File.Delete(path);
                }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException) { }
            }
            return 0;
        }, ct, true);
    }

    public Task<bool> SaveGradingPaperInfoAsync(string jobId, string? subject, double? earned, double? possible, CancellationToken ct = default)
    {
        ValidId(jobId); ValidatePaperInfo(subject, earned, possible);
        return Run(() => Transaction(ct, () =>
        {
            var input = CheckRun(jobId);
            if (input.Document.Mode == ImportMode.Blank && earned is not null) throw new DomainValidationException("Blank exams cannot have earned points.");
            return WritePaperInfo(input.Document.Id, subject, earned, possible, false) > 0;
        }), ct, true);
    }

    private int WritePaperInfo(string id, string? subject, double? earned, double? possible, bool manual) =>
        Execute("INSERT INTO paper_info(document_id,subject,earned,possible,manual) VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(document_id) DO UPDATE SET subject=excluded.subject,earned=excluded.earned,possible=excluded.possible,manual=excluded.manual WHERE paper_info.manual=0 OR excluded.manual=1", id, string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(), earned, possible, manual);

    private static void ValidatePaperInfo(string? subject, double? earned, double? possible)
    {
        if (subject?.Trim().Length > 60 || earned is { } e && (!double.IsFinite(e) || e < 0)
            || possible is { } p && (!double.IsFinite(p) || p < 0) || earned > possible)
            throw new ArgumentException("Check the subject and scores. Earned points cannot exceed possible points.");
    }
}
