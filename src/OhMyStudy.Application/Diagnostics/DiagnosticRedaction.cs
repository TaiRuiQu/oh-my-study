using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace OhMyStudy.Application.Diagnostics;

public static partial class DiagnosticRedaction
{
    public static bool IsSecret(string name) => name.Replace("-", "", StringComparison.Ordinal).Replace("_", "", StringComparison.Ordinal).ToLowerInvariant()
        is "authorization" or "proxyauthorization" or "apikey" or "xapikey" or "cookie" or "setcookie" or "password" or "secret" or "token" or "accesstoken" or "refreshtoken";

    public static string Text(string value, IReadOnlyList<string>? secrets = null)
    {
        foreach (string secret in secrets ?? [])
            if (secret.Length > 0) value = value.Replace(secret, "[REDACTED]", StringComparison.Ordinal);
        value = SecretQuery().Replace(value, "$1[REDACTED]");
        value = SecretAuthority().Replace(value, "$1[REDACTED]@");
        value = SecretJson().Replace(value, "$1\"[REDACTED]\"");
        return value;
    }

    public static string Body(string value, IReadOnlyList<string>? secrets = null)
    {
        try
        {
            var node = JsonNode.Parse(value);
            Redact(node, secrets);
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out var text)) return JsonValue.Create(Text(text, secrets))!.ToJsonString();
            return node?.ToJsonString() ?? "null";
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException) { return Text(value, secrets); }
    }

    private static void Redact(JsonNode? node, IReadOnlyList<string>? secrets)
    {
        if (node is JsonObject obj)
            foreach (string key in obj.Select(pair => pair.Key).ToList())
                if (IsSecret(key)) obj[key] = "[REDACTED]";
                else if (obj[key] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) obj[key] = Text(text, secrets);
                else Redact(obj[key], secrets);
        if (node is JsonArray array)
            for (int index = 0; index < array.Count; index++)
                if (array[index] is JsonValue scalar && scalar.TryGetValue<string>(out var text)) array[index] = Text(text, secrets);
                else Redact(array[index], secrets);
    }

    [GeneratedRegex("([?&](?:api[_-]?key|access_token|token|key|secret|password)=)[^&#\\s\"'\\\\]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretQuery();

    [GeneratedRegex("(https?://)[^/@\\s\"']+@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretAuthority();

    [GeneratedRegex("(\"(?:authorization|proxy[_-]?authorization|api[_-]?key|x[_-]?api[_-]?key|cookie|set[_-]?cookie|password|secret|token|access[_-]?token|refresh[_-]?token)\"\\s*:\\s*)\"(?:[^\"\\\\]|\\\\.)*\"", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretJson();
}
