using System.Net.Http;
using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Providers;

public sealed record CatalogMatch(string ProviderId, string ProviderName, string? Api, ModelConfig Model);

/// <summary>Explicit, credential-free lookup. Never infer capabilities or prices from a similar model ID.</summary>
public static class ModelCatalog
{
    public const int MaximumBytes = 32 * 1024 * 1024;

    public static async Task<List<CatalogMatch>> FindAsync(HttpClient http, string modelId, CancellationToken cancellationToken = default)
    {
        using var response = await http.GetAsync("https://models.dev/api.json", HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > MaximumBytes) throw new InvalidDataException("Model catalog exceeds limit");
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        byte[] buffer = new byte[16384];
        while (true)
        {
            int count = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (count == 0) break;
            if (bytes.Length + count > MaximumBytes) throw new InvalidDataException("Model catalog exceeds limit");
            bytes.Write(buffer, 0, count);
        }
        using var json = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length));
        return Find(json.RootElement, modelId);
    }

    public static List<CatalogMatch> Find(JsonElement catalog, string modelId)
    {
        var result = new List<CatalogMatch>();
        if (catalog.ValueKind != JsonValueKind.Object) throw new InvalidDataException("Invalid model catalog");
        foreach (var provider in catalog.EnumerateObject())
        {
            if (provider.Value.ValueKind != JsonValueKind.Object || !provider.Value.TryGetProperty("models", out var models)
                || models.ValueKind != JsonValueKind.Object || !models.TryGetProperty(modelId, out var model) || model.ValueKind != JsonValueKind.Object) continue;
            ulong? context = null;
            if (model.TryGetProperty("limit", out var limit) && limit.ValueKind == JsonValueKind.Object
                && limit.TryGetProperty("context", out var size) && size.ValueKind == JsonValueKind.Number && size.TryGetUInt64(out ulong tokens) && tokens > 0) context = tokens;
            bool? vision = null;
            if (model.TryGetProperty("modalities", out var modes) && modes.ValueKind == JsonValueKind.Object
                && modes.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Array)
                vision = input.EnumerateArray().Any(value => value.ValueKind == JsonValueKind.String && value.GetString() == "image");
            var config = new ModelConfig
            {
                Id = modelId,
                DisplayName = Text(model, "name"),
                ContextWindow = context,
                Currency = "USD",
                PriceInput = Price(model, "input"),
                PriceOutput = Price(model, "output"),
                Capabilities = new()
                {
                    Vision = vision,
                    Tools = Boolean(model, "tool_call"),
                    Thinking = [],
                    Source = "models.dev"
                }
            };
            result.Add(new(provider.Name, Text(provider.Value, "name") ?? provider.Name, Text(provider.Value, "api"), config));
        }
        return result.OrderBy(match => match.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static double? Price(JsonElement model, string name) =>
        model.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object && cost.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double price) && double.IsFinite(price) && price >= 0 ? price : null;
}
