using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    private const string EffectiveSubject = "COALESCE(NULLIF(q.subject,''),i.subject,'')";

    public Task<QuestionPage> QueryQuestionsAsync(QuestionQuery query, CancellationToken cancellationToken = default)
    {
        if (query.Offset < 0 || query.Limit is < 1 or > 500) throw new ArgumentOutOfRangeException(nameof(query));
        return Run(() =>
        {
            RequireQuestionSummaries();
            string scope = query.Filter switch
            {
                QuestionFilter.Mistakes => "q.mistake=1",
                QuestionFilter.NeedsReview => "q.review=1",
                QuestionFilter.Correct => "q.verdict='correct'",
                _ => "1=1"
            };
            var conditions = new List<string> { scope };
            var values = new List<object?>();
            if (!string.IsNullOrWhiteSpace(query.Search))
            {
                values.Add("%" + query.Search.Trim().Replace("\\", "\\\\", StringComparison.Ordinal).Replace("%", "\\%", StringComparison.Ordinal).Replace("_", "\\_", StringComparison.Ordinal) + "%");
                conditions.Add($"(q.search_text LIKE $p{values.Count - 1} ESCAPE '\\' OR d.name LIKE $p{values.Count - 1} ESCAPE '\\')");
            }
            if (query.Subject is { } subject)
            {
                values.Add(subject);
                conditions.Add($"{EffectiveSubject}=$p{values.Count - 1}");
            }
            const string from = " FROM questions q JOIN documents d ON d.id=q.document_id LEFT JOIN paper_info i ON i.document_id=q.document_id";
            string order = query.Sort switch
            {
                QuestionSort.Oldest => "d.created_at,d.id,q.rowid",
                QuestionSort.Exam => "d.name COLLATE NOCASE,d.created_at DESC,d.id,q.rowid",
                QuestionSort.LowestScore => "(q.earned IS NULL OR q.possible IS NULL OR q.possible=0),q.earned/q.possible,d.created_at DESC,d.id,q.rowid",
                QuestionSort.RecentlyUpdated => "q.updated_at DESC,q.rowid",
                _ => "d.created_at DESC,d.id,q.rowid"
            };
            string where = " WHERE " + string.Join(" AND ", conditions);
            var page = Query("SELECT q.id,q.document_id,d.name,q.number," + EffectiveSubject + ",q.question_type,q.preview,q.item_count,q.earned,q.possible,q.verdict,q.mistake,q.review"
                + from + where + $" ORDER BY {order} LIMIT {query.Limit} OFFSET {query.Offset}", r => new QuestionSummary(
                    r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetInt32(7),
                    r.IsDBNull(8) ? null : r.GetDouble(8), r.IsDBNull(9) ? null : r.GetDouble(9),
                    r.IsDBNull(10) ? null : DomainJson.Deserialize<Verdict>("\"" + r.GetString(10) + "\""), r.GetBoolean(11), r.GetBoolean(12)), [.. values]);
            int matching = Convert.ToInt32(Scalar("SELECT COUNT(*)" + from + where, [.. values]));
            int total = values.Count == 0 ? matching : Convert.ToInt32(Scalar("SELECT COUNT(*)" + from + " WHERE " + scope));
            return new QuestionPage(page, matching, total);
        }, cancellationToken);
    }

    public Task<List<string>> ListQuestionSubjectsAsync(CancellationToken cancellationToken = default) => Run(() =>
    {
        RequireQuestionSummaries();
        return Query("SELECT DISTINCT value FROM (SELECT " + EffectiveSubject + " AS value FROM questions q LEFT JOIN paper_info i ON i.document_id=q.document_id) WHERE value<>'' ORDER BY value", r => r.GetString(0));
    }, cancellationToken);

    public Task<SavedQuestion> GetQuestionAsync(string id, CancellationToken cancellationToken = default)
    {
        ValidId(id);
        return Run(() =>
        {
            int version = Convert.ToInt32(Scalar("PRAGMA user_version"));
            return Query("SELECT document_id,content_json,updated_at FROM questions WHERE id=$p0", r => new SavedQuestion
            {
                Id = id,
                DocumentId = r.GetString(0),
                Question = DomainJson.Deserialize<Question>(version < 4 ? LegacyQuestionFormat.UpgradeQuestion(r.GetString(1)) : r.GetString(1)),
                UpdatedAt = r.GetString(2)
            }, id).SingleOrDefault() ?? throw new KeyNotFoundException("Question not found");
        }, cancellationToken);
    }

    private void RequireQuestionSummaries()
    {
        if (Convert.ToInt32(Scalar("PRAGMA user_version")) < 4) throw new InvalidOperationException("Open the library for editing to upgrade it before browsing questions");
    }
}
