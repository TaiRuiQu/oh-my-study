using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Application.Search;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Search;

public sealed class BackgroundSearchIndexer : IAsyncDisposable
{
    private readonly LibraryRepository library;
    private readonly ISearchModelClient client;
    private readonly bool ownsClient;
    private readonly Channel<(SearchDocument Document, string? CorrelationId)> pending = Channel.CreateUnbounded<(SearchDocument, string?)>(new() { SingleReader = true });
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;

    public BackgroundSearchIndexer(LibraryRepository library, ISearchModelClient? client = null)
    {
        this.library = library;
        this.client = client ?? new SearchModelClient(new WindowsCredentialVault());
        ownsClient = client is null;
        worker = ProcessAsync();
    }

    public void Enqueue(SavedQuestion question) => pending.Writer.TryWrite((new(question.Id, QuestionText.Meaning(question.Question)), OperationDiagnostics.CorrelationId));

    private async Task ProcessAsync()
    {
        try
        {
            while (await pending.Reader.WaitToReadAsync(stop.Token).ConfigureAwait(false))
            {
                var batch = new List<SearchDocument>();
                string? correlationId = null;
                while (batch.Count < 8 && pending.Reader.TryRead(out var item))
                {
                    if (string.IsNullOrWhiteSpace(item.Document.Text)) continue;
                    batch.Add(item.Document);
                    correlationId ??= item.CorrelationId;
                }
                if (batch.Count == 0) continue;
                using var trace = OperationDiagnostics.Begin(correlationId ?? Guid.NewGuid().ToString("N"));
                var clock = Stopwatch.StartNew();
                try
                {
                    if ((await library.GetSettingsAsync(stop.Token).ConfigureAwait(false)).EmbeddingModel is not { } model) continue;
                    var values = await client.EmbedAsync(model, batch.Select(document => document.Text).ToList(), stop.Token).ConfigureAwait(false);
                    if (values.Count != batch.Count) throw new InvalidDataException("The embedding response did not match the input count");
                    var vectors = batch.Select((document, index) => new QuestionVector(document.Id, SemanticSearchService.Hash(document.Text), VectorSearch.Normalize(values[index]))).ToList();
                    if (vectors.Any(vector => vector.Values.Length != vectors[0].Values.Length)) throw new InvalidDataException("Inconsistent embedding dimensions");
                    await library.SaveSearchVectorsAsync(SemanticSearchService.ModelKey(model), vectors, stop.Token).ConfigureAwait(false);
                    OperationDiagnostics.Record("SearchIndex.Completed", new() { ["questions"] = batch.Count, ["model"] = model.Model, ["elapsed_ms"] = clock.ElapsedMilliseconds });
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    OperationDiagnostics.Record("SearchIndex.Failed", new()
                    {
                        ["question_ids"] = new JsonArray(batch.Select(document => (JsonNode?)JsonValue.Create(document.Id)).ToArray()),
                        ["elapsed_ms"] = clock.ElapsedMilliseconds,
                        ["exception"] = error.ToString()
                    }, true);
                }
            }
        }
        catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
    }

    public async ValueTask DisposeAsync()
    {
        pending.Writer.TryComplete();
        await stop.CancelAsync().ConfigureAwait(false);
        await worker.ConfigureAwait(false);
        if (ownsClient && client is IDisposable disposable) disposable.Dispose();
        stop.Dispose();
    }
}
