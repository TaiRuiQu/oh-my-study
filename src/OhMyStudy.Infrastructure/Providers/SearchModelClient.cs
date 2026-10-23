using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Search;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Diagnostics;

namespace OhMyStudy.Infrastructure.Providers;

public sealed class SearchModelException(string message) : Exception(message);

public sealed class SearchModelClient : ISearchModelClient, IDisposable
{
    private readonly HttpClient http;
    private readonly ICredentialVault vault;
    private readonly bool ownsHttp;

    public SearchModelClient(ICredentialVault vault) : this(new HttpClient(new DiagnosticHttpHandler(new SocketsHttpHandler { AllowAutoRedirect = false })) { Timeout = TimeSpan.FromMinutes(2) }, vault)
        => ownsHttp = true;

    public SearchModelClient(HttpClient http, ICredentialVault vault) { this.http = http; this.vault = vault; }

    public async Task<IReadOnlyList<float[]>> EmbedAsync(SearchModel model, IReadOnlyList<string> inputs, CancellationToken cancellationToken, bool query = false)
    {
        bool dashscope = model.ApiType == "dashscope";
        var body = dashscope ? new JsonObject
        {
            ["model"] = model.Model,
            ["input"] = new JsonObject { ["texts"] = Strings(inputs) },
            ["parameters"] = new JsonObject { ["text_type"] = query ? "query" : "document" }
        } : new JsonObject
        {
            ["model"] = model.Model,
            ["input"] = Strings(inputs),
            ["encoding_format"] = "float"
        };
        using var response = await SendAsync(model, dashscope ? "services/embeddings/text-embedding/text-embedding" : "embeddings", body, cancellationToken).ConfigureAwait(false);
        try
        {
            var data = dashscope ? response.RootElement.GetProperty("output").GetProperty("embeddings") : response.RootElement.GetProperty("data");
            if (data.GetArrayLength() != inputs.Count) throw new InvalidDataException("Unexpected embedding count");
            var values = new float[inputs.Count][];
            foreach (var item in data.EnumerateArray())
            {
                int index = item.GetProperty(dashscope ? "text_index" : "index").GetInt32();
                if (index < 0 || index >= values.Length || values[index] is not null) throw new InvalidDataException("Invalid embedding index");
                values[index] = VectorSearch.Normalize(item.GetProperty("embedding").EnumerateArray().Select(value => value.GetSingle()).ToArray());
            }
            if (values.Any(value => value is null) || values.Any(value => value.Length != values[0].Length)) throw new InvalidDataException("Inconsistent embedding dimensions");
            return values;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or InvalidDataException or OverflowException)
        { throw new SearchModelException("The embedding response is invalid. Check the endpoint and model."); }
    }

    public async Task<IReadOnlyList<RerankScore>> RerankAsync(SearchModel model, string query, IReadOnlyList<string> documents, CancellationToken cancellationToken)
    {
        bool dashscope = model.ApiType == "dashscope";
        var body = dashscope ? new JsonObject
        {
            ["model"] = model.Model,
            ["input"] = new JsonObject { ["query"] = query, ["documents"] = Strings(documents) },
            ["parameters"] = new JsonObject { ["top_n"] = documents.Count }
        } : new JsonObject
        {
            ["model"] = model.Model,
            ["query"] = query,
            ["documents"] = Strings(documents),
            ["top_n"] = documents.Count,
            ["return_documents"] = false
        };
        using var response = await SendAsync(model, dashscope ? "services/rerank/text-rerank/text-rerank" : "rerank", body, cancellationToken).ConfigureAwait(false);
        try
        {
            var output = dashscope ? response.RootElement.GetProperty("output") : response.RootElement;
            var scores = output.GetProperty("results").EnumerateArray()
                .Select(item => new RerankScore(item.GetProperty("index").GetInt32(), item.GetProperty("relevance_score").GetDouble())).ToList();
            if (scores.Count != documents.Count || scores.Select(score => score.Index).Distinct().Count() != scores.Count
                || scores.Any(score => score.Index < 0 || score.Index >= documents.Count || !double.IsFinite(score.Score))) throw new InvalidDataException("Invalid reranker results");
            return scores;
        }
        catch (Exception error) when (error is JsonException or KeyNotFoundException or InvalidOperationException or FormatException or InvalidDataException or OverflowException)
        { throw new SearchModelException("The reranker response is invalid. Check the endpoint and model, or clear the reranker configuration."); }
    }

    private async Task<JsonDocument> SendAsync(SearchModel model, string operation, JsonObject body, CancellationToken cancellationToken)
    {
        ConfigurationValidation.ValidateSearchModel(model);
        byte[]? key = null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, model.BaseUrl.TrimEnd('/') + "/" + operation);
            if (model.KeyRef is { } reference)
            {
                key = await Task.Run(() => vault.Read(reference), cancellationToken).ConfigureAwait(false);
                if (key is null) throw new SearchModelException("The search model key is unavailable. Enter it again in Settings.");
                string secret = Encoding.UTF8.GetString(key);
                if (string.IsNullOrWhiteSpace(secret) || secret.Contains('\r') || secret.Contains('\n')) throw new SearchModelException("The search model key is invalid. Enter it again in Settings.");
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", secret);
            }
            request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new SearchModelException(response.StatusCode switch
            {
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Search model authentication failed. Check its key in Settings.",
                HttpStatusCode.TooManyRequests => "The search model is rate limited. Wait and try again.",
                HttpStatusCode.BadRequest or HttpStatusCode.NotFound => "The search model rejected the request. Check its Base URL and model name.",
                _ => "The search model request failed. Check the service and try again."
            });
            await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
            using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new SearchModelException("The search model timed out. Try again."); }
        catch (Exception error) when (error is HttpRequestException or IOException or JsonException)
        { throw new SearchModelException("Could not connect to the search model or read its response. Check the service and try again."); }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private static JsonArray Strings(IReadOnlyList<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
    public void Dispose() { if (ownsHttp) http.Dispose(); }
}
