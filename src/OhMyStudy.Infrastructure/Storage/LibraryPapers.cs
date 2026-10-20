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
        if (string.IsNullOrWhiteSpace(name) || name.Trim().Length > 240) throw new ArgumentException("标题需为 1–240 个字。");
        return Run(() => Transaction(ct, () =>
        {
            DocumentIdle(id);
            if (Execute("UPDATE documents SET name=$p0 WHERE id=$p1", name.Trim(), id) != 1) throw new KeyNotFoundException();
            WritePaperInfo(id, subject, earned, possible, true); return 0;
        }), ct, true);
    }

    public Task<bool> SaveGradingPaperInfoAsync(string jobId, string? subject, double? earned, double? possible, CancellationToken ct = default)
    {
        ValidId(jobId); ValidatePaperInfo(subject, earned, possible);
        return Run(() => Transaction(ct, () =>
        {
            var input = CheckRun(jobId);
            if (input.Document.Mode == ImportMode.Blank && earned is not null) throw new DomainValidationException("空白试卷不能填写得分。");
            return WritePaperInfo(input.Document.Id, subject, earned, possible, false) > 0;
        }), ct, true);
    }

    private int WritePaperInfo(string id, string? subject, double? earned, double? possible, bool manual) =>
        Execute("INSERT INTO paper_info(document_id,subject,earned,possible,manual) VALUES($p0,$p1,$p2,$p3,$p4) ON CONFLICT(document_id) DO UPDATE SET subject=excluded.subject,earned=excluded.earned,possible=excluded.possible,manual=excluded.manual WHERE paper_info.manual=0 OR excluded.manual=1", id, string.IsNullOrWhiteSpace(subject) ? null : subject.Trim(), earned, possible, manual);

    private static void ValidatePaperInfo(string? subject, double? earned, double? possible)
    {
        if (subject?.Trim().Length > 60 || earned is { } e && (!double.IsFinite(e) || e < 0)
            || possible is { } p && (!double.IsFinite(p) || p < 0) || earned > possible)
            throw new ArgumentException("请检查学科和分数，得分不能超过满分。");
    }
}
