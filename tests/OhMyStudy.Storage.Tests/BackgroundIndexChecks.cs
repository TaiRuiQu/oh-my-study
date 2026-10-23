using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Application.Search;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Search;
using OhMyStudy.Infrastructure.Storage;

internal static partial class Program
{
    private static async Task BackgroundIndexChecks(LibraryRepository library, SavedQuestion first, SavedQuestion second, SearchModel model)
    {
        await library.ClearSearchVectorsAsync();
        var client = new BackgroundEmbeddingClient();
        await using var indexer = new BackgroundSearchIndexer(library, client);
        indexer.Enqueue(first);
        await client.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Check((await library.ReadSearchVectorsAsync(SemanticSearchService.ModelKey(model))).Count == 0, "background embedding can wait for the network after Enqueue returns");
        await library.SaveQuestionNotesAsync(first.Id, "private-note-marker");
        indexer.Enqueue(second);
        client.Release.TrySetResult();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        IReadOnlyList<QuestionVector> vectors;
        do
        {
            await Task.Delay(10, timeout.Token);
            vectors = await library.ReadSearchVectorsAsync(SemanticSearchService.ModelKey(model), timeout.Token);
        } while (vectors.Count != 2);
        Check(vectors.Single(vector => vector.QuestionId == first.Id).ContentHash == SemanticSearchService.Hash(QuestionText.Meaning(first.Question))
            && client.Inputs.All(text => !text.Contains("private-note-marker", StringComparison.Ordinal) && !text.Contains("private-answer-marker", StringComparison.Ordinal)), "automatic indexing persists content hashes and excludes private notes and student answers");

        var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var recovered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        void Log(string phase, string detail, bool failure)
        {
            if (phase == "SearchIndex.Failed" && failure && detail.Contains("synthetic embedding failure", StringComparison.Ordinal)) failed.TrySetResult();
            if (phase == "SearchIndex.Completed" && failed.Task.IsCompleted) recovered.TrySetResult();
        }
        OperationDiagnostics.Written += Log;
        try
        {
            client.Fail = true;
            indexer.Enqueue(first);
            await failed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check((await library.ReadSearchVectorsAsync(SemanticSearchService.ModelKey(model))).Count == 2, "background network failure preserves saved questions and previous vectors");
            client.Fail = false;
            indexer.Enqueue(second);
            await recovered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Check(true, "background indexing continues after a failed batch");
        }
        finally { OperationDiagnostics.Written -= Log; }
    }

    private sealed class BackgroundEmbeddingClient : ISearchModelClient
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public List<string> Inputs { get; } = [];
        public bool Fail { get; set; }

        public async Task<IReadOnlyList<float[]>> EmbedAsync(SearchModel model, IReadOnlyList<string> inputs, CancellationToken cancellationToken, bool query = false)
        {
            if (query || inputs.Count > 8) throw new InvalidOperationException("Unexpected background embedding input");
            Entered.TrySetResult();
            await Release.Task.WaitAsync(cancellationToken);
            if (Fail) throw new IOException("synthetic embedding failure");
            Inputs.AddRange(inputs);
            return inputs.Select(_ => new float[] { 1, 0 }).ToList();
        }

        public Task<IReadOnlyList<RerankScore>> RerankAsync(SearchModel model, string query, IReadOnlyList<string> documents, CancellationToken cancellationToken) => throw new InvalidOperationException("Indexing must not rerank");
    }
}
