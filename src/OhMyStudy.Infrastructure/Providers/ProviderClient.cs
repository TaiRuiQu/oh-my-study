using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Diagnostics;

namespace OhMyStudy.Infrastructure.Providers;

/// <summary>A secret argument deliberately excluded from JSON, records, and diagnostic text.</summary>
public sealed class ProviderCredentials
{
    internal string Key { get; }
    public ProviderCredentials(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Contains('\r') || key.Contains('\n')) throw ProviderProtocol.Invalid("missing or invalid provider key");
        Key = key;
    }
    public override string ToString() => "[provider credentials]";
}

public interface IModelClient
{
    Protocol Protocol { get; }
    Task<Turn> CallAsync(ProviderCredentials credentials, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools, Action<ProviderProgress>? progress = null, CancellationToken cancellationToken = default);
    Task<ModelPage> ListModelsAsync(ProviderCredentials credentials, string? cursor = null, CancellationToken cancellationToken = default);
}

/// <summary>Explicit opt-in retries of HTTP rejections only. Ambiguous network failures never replay.</summary>
public sealed record ProviderRetryPolicy
{
    public int MaximumAttempts { get; init; } = 1;
    public TimeSpan InitialDelay { get; init; } = TimeSpan.FromSeconds(1);
    public TimeSpan MaximumDelay { get; init; } = TimeSpan.FromSeconds(30);
    internal void Validate()
    {
        if (MaximumAttempts is < 1 or > 4 || InitialDelay < TimeSpan.Zero || MaximumDelay < InitialDelay || MaximumDelay > TimeSpan.FromMinutes(2)) throw ProviderProtocol.Invalid("invalid retry bounds");
    }
}

/// <summary>
/// Constructing a client performs no network requests. Only explicit calls send traffic.
/// The default handler validates TLS certificates and rejects redirects. Injected handlers must do the same.
/// </summary>
public sealed class ProviderClient : IModelClient, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly ResponsesSdkClient? _responses;
    private readonly MessagesSdkClient? _messages;
    public Protocol Protocol { get; }

    public ProviderClient(Protocol protocol, string baseUri, IReadOnlyDictionary<string, string>? headers = null)
        // SDK streams run until the caller cancels; each SDK bounds non-streaming calls itself.
        : this(new HttpClient(new DiagnosticHttpHandler(new SocketsHttpHandler { AllowAutoRedirect = false })) { Timeout = Timeout.InfiniteTimeSpan }, protocol, baseUri, headers)
        => _ownsHttp = true;

    public ProviderClient(HttpClient httpClient, Protocol protocol, string baseUri, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (!Enum.IsDefined(protocol)) throw ProviderProtocol.Invalid("unknown protocol");
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || (uri.Scheme != "https" && !(uri.Scheme == "http" && IsLoopback(uri)))) throw ProviderProtocol.Invalid("provider URL must be HTTPS without embedded credentials, query or fragment");
        var endpoint = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        _http = httpClient; Protocol = protocol;
        var customHeaders = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            if (name.Equals("authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) || name.Equals("host", StringComparison.OrdinalIgnoreCase)) throw ProviderProtocol.Invalid("credentials must use private credential argument");
            using var check = new HttpRequestMessage();
            try { check.Headers.Add(name, value); }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException) { throw ProviderProtocol.Invalid("invalid custom header"); }
            customHeaders[name] = value;
        }
        if (OpenRouter.IsEndpoint(endpoint)) OpenRouter.AddAttribution(customHeaders);
        if (protocol == Protocol.Responses) _responses = new ResponsesSdkClient(httpClient, endpoint, customHeaders);
        if (protocol == Protocol.Messages) _messages = new MessagesSdkClient(httpClient, endpoint, customHeaders);
    }

    public Task<Turn> CallAsync(ProviderCredentials credentials, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools, Action<ProviderProgress>? progress = null, CancellationToken cancellationToken = default)
        => CallWithRetryAsync(credentials, options, history, tools, new ProviderRetryPolicy(), progress, cancellationToken);

    /// <summary>A bounded, single-attempt probe with fixed text and no document/history/tool inputs.</summary>
    public Task<Turn> TestConnectionAsync(ProviderCredentials credentials, string modelId, CancellationToken cancellationToken = default)
    {
        if (_messages is not null) return _messages.TestConnectionAsync(credentials, modelId, cancellationToken);
        var options = new RequestOptions { Model = Model.Manual(modelId), MaxOutputTokens = 256, Stream = false, AllowUnknownCapabilities = true, Extra = [] };
        return CallAsync(credentials, options, ProviderProtocol.Initial(Protocol, "Reply with OK.", []), [], cancellationToken: cancellationToken);
    }

    public async Task<Turn> CallWithRetryAsync(ProviderCredentials credentials, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools, ProviderRetryPolicy retryPolicy, Action<ProviderProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        retryPolicy.Validate();
        if (cancellationToken.IsCancellationRequested) throw new ProviderException(ErrorKind.Cancelled, "provider call cancelled");
        if (_responses is not null)
            return await CallSdkWithRetryAsync(retryPolicy, cancellationToken, () => _responses.CallAsync(credentials, options, history, tools, progress, cancellationToken)).ConfigureAwait(false);
        if (_messages is not null)
            return await CallSdkWithRetryAsync(retryPolicy, cancellationToken, () => _messages.CallAsync(credentials, options, history, tools, progress, cancellationToken)).ConfigureAwait(false);
        throw ProviderProtocol.Invalid("unknown protocol");
    }

    /// <summary>Shared explicit retry loop for SDK-backed protocols. Ambiguous failures never replay.</summary>
    private static async Task<Turn> CallSdkWithRetryAsync(ProviderRetryPolicy retryPolicy, CancellationToken cancellationToken, Func<Task<Turn>> call)
    {
        for (var attempt = 1; ; attempt++)
        {
            try { return await call().ConfigureAwait(false); }
            catch (ProviderException error) when (attempt < retryPolicy.MaximumAttempts && error.IsHttpRejection && error.Error.Status is 429 or >= 500 && error.Retryable)
            {
                await DelayAsync(error.Error.RetryAfter, attempt, retryPolicy, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    public async Task<ModelPage> ListModelsAsync(ProviderCredentials credentials, string? cursor = null, CancellationToken cancellationToken = default)
    {
        if (_responses is not null)
            return await _responses.ListModelsAsync(credentials, cursor, cancellationToken).ConfigureAwait(false);
        if (_messages is not null)
            return await _messages.ListModelsAsync(credentials, cursor, cancellationToken).ConfigureAwait(false);
        throw ProviderProtocol.Invalid("unknown protocol");
    }

    private static bool IsLoopback(Uri uri) => uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host is "127.0.0.1" or "[::1]" or "::1";
    private static async Task DelayAsync(string? retryAfter, int attempt, ProviderRetryPolicy policy, CancellationToken cancellationToken)
    {
        try { await Task.Delay(RetryDelay(retryAfter, attempt, policy), cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new ProviderException(ErrorKind.Cancelled, "provider retry cancelled"); }
    }

    internal static TimeSpan RetryDelay(string? retryAfter, int attempt, ProviderRetryPolicy policy)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(policy.MaximumDelay.TotalMilliseconds, policy.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
        if (int.TryParse(retryAfter, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0) delay = TimeSpan.FromSeconds(seconds);
        else if (DateTimeOffset.TryParse(retryAfter, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)) delay = date - DateTimeOffset.UtcNow;
        delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, policy.MaximumDelay.TotalMilliseconds));
        return delay;
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
