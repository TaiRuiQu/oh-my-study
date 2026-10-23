namespace OhMyStudy.Infrastructure.Providers;

/// <summary>OpenRouter-only headers and cache routing. Other gateways never receive them.</summary>
internal static class OpenRouter
{
    public const string Homepage = "https://github.com/TaiRuiQu/oh-my-study";
    public const string Title = "Oh My Study";

    public static bool IsEndpoint(Uri endpoint) => endpoint.Host.Equals("openrouter.ai", StringComparison.OrdinalIgnoreCase);

    /// <summary>Attribution names this app in OpenRouter rankings; the referring URL is its identity.</summary>
    public static void AddAttribution(IDictionary<string, string> headers)
    {
        headers.TryAdd("HTTP-Referer", Homepage);
        headers.TryAdd("X-OpenRouter-Title", Title);
    }

    public static bool IsClaude(string modelId) => modelId.StartsWith("anthropic/", StringComparison.OrdinalIgnoreCase);

    public static bool IsGemini(string modelId) => modelId.StartsWith("google/", StringComparison.OrdinalIgnoreCase);
}
