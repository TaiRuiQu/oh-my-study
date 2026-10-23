using System.Security.Cryptography;
using System.Text;
using OhMyStudy.Domain;
using OhMyStudy.Application.Diagnostics;

namespace OhMyStudy.Application.Search;

public sealed record SearchScope(string? Subject = null, bool Mistakes = false, bool NeedsReview = false, bool Correct = false);
public sealed record SearchDocument(string Id, string Text);
public sealed record RerankScore(int Index, double Score);
public sealed record SearchProgress(int Indexed, int Total, bool Reranking = false);
public sealed record SearchPlan(string ModelKey, IReadOnlyList<SearchDocument> Documents, IReadOnlyList<QuestionVector> Cached)
{
    public IReadOnlyList<SearchDocument> Pending
    {
        get
        {
            var hashes = Cached.ToDictionary(vector => vector.QuestionId, vector => vector.ContentHash, StringComparer.Ordinal);
            return Documents.Where(document => !hashes.TryGetValue(document.Id, out string? hash) || hash != SemanticSearchService.Hash(document.Text)).ToList();
        }
    }
}

public interface ISemanticSearchStore
{
    Task<IReadOnlyList<SearchDocument>> ReadSearchDocumentsAsync(SearchScope scope, CancellationToken cancellationToken);
    Task<IReadOnlyList<QuestionVector>> ReadSearchVectorsAsync(string modelKey, CancellationToken cancellationToken);
    Task SaveSearchVectorsAsync(string modelKey, IReadOnlyList<QuestionVector> vectors, CancellationToken cancellationToken);
}

public interface ISearchModelClient
{
    Task<IReadOnlyList<float[]>> EmbedAsync(SearchModel model, IReadOnlyList<string> inputs, CancellationToken cancellationToken, bool query = false);
    Task<IReadOnlyList<RerankScore>> RerankAsync(SearchModel model, string query, IReadOnlyList<string> documents, CancellationToken cancellationToken);
}

public sealed class SemanticSearchService(ISemanticSearchStore store, ISearchModelClient client)
{
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    public static string ModelKey(SearchModel model) => Hash("question-meaning-v1\n" + model.BaseUrl.TrimEnd('/') + "\n" + model.Model + (model.ApiType == "openai" ? "" : "\n" + model.ApiType));

    public async Task<SearchPlan> PrepareAsync(SearchModel model, SearchScope scope, CancellationToken cancellationToken = default)
    {
        ConfigurationValidation.ValidateSearchModel(model);
        string key = ModelKey(model);
        var documents = await store.ReadSearchDocumentsAsync(scope, cancellationToken).ConfigureAwait(false);
        var cached = await store.ReadSearchVectorsAsync(key, cancellationToken).ConfigureAwait(false);
        var plan = new SearchPlan(key, documents.Where(document => !string.IsNullOrWhiteSpace(document.Text)).ToList(), cached);
        OperationDiagnostics.Record("SearchIndex.Prepared", new() { ["documents"] = plan.Documents.Count, ["cached"] = cached.Count, ["pending"] = plan.Pending.Count });
        return plan;
    }

    public async Task<IReadOnlyList<VectorMatch>> SearchAsync(SearchPlan plan, SearchModel embedding, SearchModel? reranker, string query,
        IProgress<SearchProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length > 6000) throw new ArgumentException("Enter a query of 1 to 6000 characters", nameof(query));
        if (plan.ModelKey != ModelKey(embedding)) throw new InvalidOperationException("The embedding model changed. Start the search again.");
        if (plan.Documents.Count == 0) return [];
        var hashes = plan.Documents.ToDictionary(document => document.Id, document => Hash(document.Text), StringComparer.Ordinal);
        var vectors = plan.Cached.Where(vector => hashes.TryGetValue(vector.QuestionId, out string? hash) && hash == vector.ContentHash).ToDictionary(vector => vector.QuestionId, StringComparer.Ordinal);
        var pending = plan.Documents.Where(document => !vectors.ContainsKey(document.Id)).ToList();
        int completed = plan.Documents.Count - pending.Count;
        progress?.Report(new(completed, plan.Documents.Count));
        foreach (var batch in pending.Chunk(8))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var values = await client.EmbedAsync(embedding, batch.Select(document => document.Text).ToList(), cancellationToken).ConfigureAwait(false);
            if (values.Count != batch.Length) throw new InvalidDataException("The embedding response did not match the input count");
            var saved = batch.Select((document, index) => new QuestionVector(document.Id, hashes[document.Id], VectorSearch.Normalize(values[index]))).ToList();
            int dimensions = vectors.Values.FirstOrDefault()?.Values.Length ?? saved[0].Values.Length;
            if (saved.Any(vector => vector.Values.Length != dimensions)) throw new InvalidDataException("Embedding dimensions changed. Rebuild the semantic search index.");
            await store.SaveSearchVectorsAsync(plan.ModelKey, saved, cancellationToken).ConfigureAwait(false);
            foreach (var vector in saved) vectors[vector.QuestionId] = vector;
            completed += batch.Length;
            progress?.Report(new(completed, plan.Documents.Count));
        }
        var queryValues = await client.EmbedAsync(embedding, [query.Trim()], cancellationToken, query: true).ConfigureAwait(false);
        if (queryValues.Count != 1) throw new InvalidDataException("Expected one query embedding");
        var matches = VectorSearch.Find(queryValues[0], vectors.Values, 50, cancellationToken);
        if (reranker is null || matches.Count == 0) return matches;
        progress?.Report(new(completed, plan.Documents.Count, true));
        var texts = plan.Documents.ToDictionary(document => document.Id, document => document.Text, StringComparer.Ordinal);
        var scores = await client.RerankAsync(reranker, query.Trim(), matches.Select(match => texts[match.QuestionId]).ToList(), cancellationToken).ConfigureAwait(false);
        if (scores.Count != matches.Count || scores.Select(score => score.Index).Distinct().Count() != matches.Count
            || scores.Any(score => score.Index < 0 || score.Index >= matches.Count || !double.IsFinite(score.Score)))
            throw new InvalidDataException("The reranker returned invalid or incomplete results");
        return scores.OrderByDescending(score => score.Score).ThenBy(score => score.Index).Select(score => new VectorMatch(matches[score.Index].QuestionId, score.Score)).ToList();
    }
}
