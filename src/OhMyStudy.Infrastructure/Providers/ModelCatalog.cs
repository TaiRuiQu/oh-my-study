using System.Net.Http;
using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Providers;

public sealed record CatalogMatch(string ProviderId, string ProviderName, string? Api, ModelConfig Model);

/// <summary>Explicit, credential-free lookup. Never infer capabilities or prices from a similar model ID.</summary>
public static class ModelCatalog
{
    public const int MaximumBytes = 32 * 1024 * 1024;

    private static readonly DailyJsonFeed Feed = new("https://models.dev/api.json", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMyStudy", "Cache", "models.json"), MaximumBytes,
        value => value.ValueKind == JsonValueKind.Object && value.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.Object && p.Value.TryGetProperty("models", out var models) && models.ValueKind == JsonValueKind.Object));
    public static async Task<List<CatalogMatch>> FindAsync(HttpClient http, string modelId, CancellationToken cancellationToken = default)
        => Find((await Feed.GetAsync(http, cancellationToken).ConfigureAwait(false)).Value, modelId);
    public static Task<CachedJson> GetAsync(HttpClient http, CancellationToken ct = default) => Feed.GetAsync(http, ct);

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
            var thinking = new List<Thinking>(); bool budget = false;
            if (model.TryGetProperty("reasoning_options", out var reasoning) && reasoning.ValueKind == JsonValueKind.Array)
                foreach (var option in reasoning.EnumerateArray())
                {
                    if (option.ValueKind != JsonValueKind.Object) continue;
                    if (Text(option, "type") == "budget_tokens") budget = true;
                    if (Text(option, "type") != "effort" || !option.TryGetProperty("values", out var values) || values.ValueKind != JsonValueKind.Array) continue;
                    foreach (var value in values.EnumerateArray())
                        if (value.ValueKind == JsonValueKind.String && value.GetString() is { } effort && new[] { "none", "minimal", "low", "medium", "high", "xhigh", "max" }.Contains(effort))
                            thinking.Add(new ResponsesThinking { Effort = effort });
                }
            var config = new ModelConfig
            {
                Id = modelId,
                DisplayName = Text(model, "name"),
                ContextWindow = context,
                Currency = "USD",
                MaxOutputTokens = model.TryGetProperty("limit", out var limits) && limits.ValueKind == JsonValueKind.Object && limits.TryGetProperty("output", out var output) && output.ValueKind == JsonValueKind.Number && output.TryGetUInt64(out var maxOutput) && maxOutput > 0 ? maxOutput : null,
                InputModalities = Modalities(model, "input"),
                OutputModalities = Modalities(model, "output"),
                Reasoning = Boolean(model, "reasoning"),
                PriceInput = Price(model, "input"),
                PriceOutput = Price(model, "output"),
                Capabilities = new()
                {
                    Vision = vision,
                    Tools = Boolean(model, "tool_call"),
                    Thinking = thinking,
                    MessagesBudget = budget,
                    Source = "models.dev"
                }
            };
            result.Add(new(provider.Name, Text(provider.Value, "name") ?? provider.Name, Text(provider.Value, "api"), config));
        }
        return result.OrderBy(match => match.ProviderName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static List<string>? Modalities(JsonElement model, string kind) => model.TryGetProperty("modalities", out var modes) && modes.ValueKind == JsonValueKind.Object && modes.TryGetProperty(kind, out var values) && values.ValueKind == JsonValueKind.Array
        ? values.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!).ToList() : null;

    // Catalog effort values are protocol-neutral; only expose values the selected protocol accepts.
    public static ModelConfig ForProtocol(ModelConfig model, Protocol protocol) => model with
    {
        Capabilities = model.Capabilities with
        {
            Thinking = protocol == Protocol.Responses ? model.Capabilities.Thinking : model.Capabilities.Thinking.OfType<ResponsesThinking>()
                .Where(t => new[] { "low", "medium", "high", "max" }.Contains(t.Effort)).Select(t => (Thinking)new AdaptiveThinking { Effort = t.Effort }).ToList(),
            MessagesBudget = protocol == Protocol.Messages && model.Capabilities.MessagesBudget == true
        }
    };

    private static string? Text(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static bool? Boolean(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null;
    private static double? Price(JsonElement model, string name) =>
        model.TryGetProperty("cost", out var cost) && cost.ValueKind == JsonValueKind.Object && cost.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out double price) && double.IsFinite(price) && price >= 0 ? price : null;
}
