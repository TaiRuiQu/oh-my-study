using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Providers;

/// <summary>Contains only sanitized local messages, never raw provider error text.</summary>
public sealed class ProviderException : Exception
{
    public ProviderError Error { get; }
    public ErrorKind Kind => Error.Kind;
    public bool Retryable => Error.Retryable;
    internal bool IsHttpRejection { get; set; }
    public ProviderException(ErrorKind kind, string message, ushort? status = null, string? retryAfter = null)
        : base(message) => Error = new ProviderError { Kind = kind, Message = message, Status = status, RetryAfter = retryAfter, Retryable = kind is ErrorKind.RateLimit or ErrorKind.Server or ErrorKind.Network };
}

public static class ProviderErrors
{
    public static ProviderException Classify(ushort status, JsonElement body, string? retryAfter = null)
    {
        var native = ProviderProtocol.Get(body, "response");
        var error = ProviderProtocol.Get(native.ValueKind == JsonValueKind.Object ? native : body, "error");
        var code = ProviderProtocol.Str(error, "code") ?? ProviderProtocol.Str(error, "type") ?? "";
        var message = (ProviderProtocol.Str(error, "message") ?? "").ToLowerInvariant();
        var kind = code is "insufficient_quota" or "billing_error" or "credit_balance_too_low" || message.Contains("credit balance", StringComparison.Ordinal) ? ErrorKind.Quota
            : status is 401 or 403 ? ErrorKind.Authentication
            : status == 429 ? ErrorKind.RateLimit
            : code.Contains("unsupported", StringComparison.Ordinal) || status is 400 or 422 && (message.Contains("unsupported", StringComparison.Ordinal) || message.Contains("not supported", StringComparison.Ordinal) || message.Contains("unknown parameter", StringComparison.Ordinal)) ? ErrorKind.UnsupportedParameter
            : status >= 500 || code is "overloaded_error" or "api_error" or "server_error" or "internal_error" ? ErrorKind.Server
            : ErrorKind.InvalidRequest;
        return new ProviderException(kind, "provider rejected request; review model, parameters and account settings", status, retryAfter);
    }
}
