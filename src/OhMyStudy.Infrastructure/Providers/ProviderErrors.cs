using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Providers;

/// <summary>Ordinary requests keep local reason codes; fixed connection probes may retain a redacted error message.</summary>
public sealed class ProviderException : Exception
{
    public ProviderError Error { get; }
    public ErrorKind Kind => Error.Kind;
    public bool Retryable => Error.Retryable;
    internal bool IsHttpRejection { get; set; }
    public string? Diagnostic { get; internal set; }
    public string? RequestId { get; internal set; }
    public string? ConnectionDetail { get; internal set; }
    public ProviderException(ErrorKind kind, string message, ushort? status = null, string? retryAfter = null)
        : base(message) => Error = new ProviderError { Kind = kind, Message = message, Status = status, RetryAfter = retryAfter, Retryable = kind is ErrorKind.RateLimit or ErrorKind.Server or ErrorKind.Network };

    // Developer logs need actionable metadata. Message stays suitable for existing UI flows;
    // Ordinary requests retain neither the provider body nor its free-form message.
    public override string ToString() => base.ToString() +
        $"\nProvider diagnostics: HTTP={Error.Status?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "none"}; Kind={Kind}; Reason={Diagnostic ?? "unclassified"}; RequestId={RequestId ?? "none"}" +
        (ConnectionDetail is null ? "" : "\nProvider connection response: " + ConnectionDetail);
}

public static class ProviderErrors
{
    public static ProviderException Classify(ushort status, JsonElement body, string? retryAfter = null, string? requestId = null)
    {
        var native = ProviderProtocol.Get(body, "response");
        var error = ProviderProtocol.Get(native.ValueKind == JsonValueKind.Object ? native : body, "error");
        var code = ProviderProtocol.Str(error, "code") ?? ProviderProtocol.Str(error, "type") ?? "";
        var message = (ProviderProtocol.Str(error, "message") ?? "").ToLowerInvariant();
        var kind = status == 402 || code is "insufficient_quota" or "billing_error" or "credit_balance_too_low" || message.Contains("credit balance", StringComparison.Ordinal) ? ErrorKind.Quota
            : status is 401 or 403 ? ErrorKind.Authentication
            : status == 429 ? ErrorKind.RateLimit
            : code.Contains("unsupported", StringComparison.Ordinal) || status is 400 or 422 && (message.Contains("unsupported", StringComparison.Ordinal) || message.Contains("not supported", StringComparison.Ordinal) || message.Contains("unknown parameter", StringComparison.Ordinal)) ? ErrorKind.UnsupportedParameter
            : status >= 500 || code is "overloaded_error" or "api_error" or "server_error" or "internal_error" ? ErrorKind.Server
            : ErrorKind.InvalidRequest;
        return new ProviderException(kind, "provider rejected request; review model, parameters and account settings", status, retryAfter)
        {
            Diagnostic = Diagnose(status, code, message, kind),
            RequestId = SafeRequestId(requestId) ?? SafeRequestId(ProviderProtocol.Str(body, "request_id"))
        };
    }

    private static string Diagnose(ushort status, string code, string message, ErrorKind kind)
    {
        // Return only local constants: gateways can echo credentials or document text in any field.
        if (code == "upgrade_required" || status == 403 && message.Contains("plan", StringComparison.Ordinal)) return "api_plan_required";
        if (kind == ErrorKind.Authentication) return status == 403 ? "permission_denied" : "credential_rejected";
        if (kind == ErrorKind.Quota) return "billing_or_credit_limit";
        if (kind == ErrorKind.RateLimit) return "rate_limit";
        if (kind == ErrorKind.Server) return "upstream_service_error";
        if (status == 413) return "request_too_large";
        if (status == 404) return "model_or_endpoint_not_found";
        if (code == "unsupported_model") return "model_not_available";
        if (message.Contains("endpoint", StringComparison.Ordinal) || message.Contains("wrong route", StringComparison.Ordinal)) return "model_endpoint_mismatch";
        if (message.Contains("cache_control", StringComparison.Ordinal)) return "cache_control_rejected";
        if (message.Contains("input_schema", StringComparison.Ordinal) || message.Contains("json schema", StringComparison.Ordinal)
            || message.Contains("$ref", StringComparison.Ordinal)) return "tool_schema_rejected";
        if (message.Contains("budget_tokens", StringComparison.Ordinal)) return "thinking_budget_rejected";
        if (message.Contains("effort", StringComparison.Ordinal)) return "thinking_effort_rejected";
        if (message.Contains("thinking", StringComparison.Ordinal)) return "thinking_configuration_rejected";
        if (message.Contains("max_tokens", StringComparison.Ordinal) || message.Contains("max_output_tokens", StringComparison.Ordinal)) return "output_token_limit_rejected";
        if (message.Contains("model", StringComparison.Ordinal)) return "model_configuration_rejected";
        return kind == ErrorKind.UnsupportedParameter ? "unsupported_parameter" : "request_rejected";
    }

    private static string? SafeRequestId(string? value) => value is { Length: >= 12 and <= 128 }
        && (value.StartsWith("req_", StringComparison.Ordinal) || Guid.TryParse(value, out _))
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-') ? value : null;
}
