using System.Buffers.Binary;
using OhMyStudy.Application.Search;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository : ISemanticSearchStore
{
    public Task<IReadOnlyList<SearchDocument>> ReadSearchDocumentsAsync(SearchScope scope, CancellationToken cancellationToken = default) => Run<IReadOnlyList<SearchDocument>>(() =>
    {
        RequireQuestionSummaries();
        var conditions = new List<string> { "1=1" };
        if (scope.Mistakes) conditions.Add("q.mistake=1");
        if (scope.NeedsReview) conditions.Add("q.review=1");
        if (scope.Correct) conditions.Add("q.verdict='correct'");
        if (scope.Subject is not null) conditions.Add(EffectiveSubject + "=$p0");
        return Query("SELECT q.id,q.content_json FROM questions q LEFT JOIN paper_info i ON i.document_id=q.document_id WHERE " + string.Join(" AND ", conditions),
            row => new SearchDocument(row.GetString(0), QuestionText.Meaning(DomainJson.Deserialize<Question>(row.GetString(1)))), scope.Subject is null ? [] : [scope.Subject]);
    }, cancellationToken);

    public Task<IReadOnlyList<QuestionVector>> ReadSearchVectorsAsync(string modelKey, CancellationToken cancellationToken = default) => Run<IReadOnlyList<QuestionVector>>(() =>
    {
        if (Scalar("SELECT name FROM sqlite_master WHERE type='table' AND name='question_embeddings'") is null) return [];
        return Query("SELECT question_id,content_hash,vector FROM question_embeddings WHERE model_key=$p0", row =>
        {
            var bytes = (byte[])row.GetValue(2);
            if (bytes.Length is < 4 or > 262144 || bytes.Length % 4 != 0) throw new InvalidDataException("Invalid cached embedding");
            var values = new float[bytes.Length / 4];
            for (int index = 0; index < values.Length; index++) values[index] = BinaryPrimitives.ReadSingleLittleEndian(bytes.AsSpan(index * 4, 4));
            return new QuestionVector(row.GetString(0), row.GetString(1), values);
        }, modelKey);
    }, cancellationToken);

    public Task SaveSearchVectorsAsync(string modelKey, IReadOnlyList<QuestionVector> vectors, CancellationToken cancellationToken = default) => Run(() => Transaction(cancellationToken, () =>
    {
        Execute("CREATE TABLE IF NOT EXISTS question_embeddings (question_id TEXT NOT NULL REFERENCES questions(id) ON DELETE CASCADE,model_key TEXT NOT NULL,content_hash TEXT NOT NULL,vector BLOB NOT NULL,PRIMARY KEY(question_id,model_key))");
        foreach (var vector in vectors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = VectorSearch.Normalize(vector.Values);
            var bytes = new byte[values.Length * 4];
            for (int index = 0; index < values.Length; index++) BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(index * 4, 4), values[index]);
            Execute("INSERT INTO question_embeddings(question_id,model_key,content_hash,vector) SELECT id,$p1,$p2,$p3 FROM questions WHERE id=$p0 ON CONFLICT(question_id,model_key) DO UPDATE SET content_hash=excluded.content_hash,vector=excluded.vector",
                vector.QuestionId, modelKey, vector.ContentHash, bytes);
        }
        return 0;
    }), cancellationToken, true);

    public Task ClearSearchVectorsAsync(CancellationToken cancellationToken = default) => Run(() =>
    {
        if (Scalar("SELECT name FROM sqlite_master WHERE type='table' AND name='question_embeddings'") is not null) Execute("DELETE FROM question_embeddings");
        return 0;
    }, cancellationToken, true);

    public Task<IReadOnlyList<QuestionSummary>> ReadRankedQuestionsAsync(IReadOnlyList<string> ids, QuestionQuery scope, CancellationToken cancellationToken = default)
    {
        if (ids.Count > 50) throw new ArgumentOutOfRangeException(nameof(ids));
        return Run<IReadOnlyList<QuestionSummary>>(() =>
        {
            if (ids.Count == 0) return [];
            string parameters = string.Join(',', Enumerable.Range(0, ids.Count).Select(index => "$p" + index));
            var found = ReadQuestionSummaries(" WHERE q.id IN (" + parameters + ")", ids.Cast<object?>().ToArray())
                .Where(summary => (scope.Subject is null || summary.Subject == scope.Subject) && (scope.Filter switch
                {
                    QuestionFilter.Mistakes => summary.Mistake,
                    QuestionFilter.NeedsReview => summary.NeedsReview,
                    QuestionFilter.Correct => summary.Verdict == Verdict.Correct,
                    _ => true
                })).ToDictionary(summary => summary.Id, StringComparer.Ordinal);
            return ids.Where(found.ContainsKey).Select(id => found[id]).ToList();
        }, cancellationToken);
    }
}
