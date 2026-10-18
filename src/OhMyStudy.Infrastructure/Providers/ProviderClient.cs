using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;

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
    private readonly Uri _base;
    private readonly Dictionary<string, string> _headers;
    public Protocol Protocol { get; }

    public ProviderClient(Protocol protocol, string baseUri, IReadOnlyDictionary<string, string>? headers = null)
        : this(new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(180) }, protocol, baseUri, headers)
        => _ownsHttp = true;

    public ProviderClient(HttpClient httpClient, Protocol protocol, string baseUri, IReadOnlyDictionary<string, string>? headers = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        if (!Enum.IsDefined(protocol)) throw ProviderProtocol.Invalid("unknown protocol");
        if (!Uri.TryCreate(baseUri, UriKind.Absolute, out var uri) || uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0 || (uri.Scheme != "https" && !(uri.Scheme == "http" && IsLoopback(uri)))) throw ProviderProtocol.Invalid("provider URL must be HTTPS without embedded credentials, query or fragment");
        _base = new Uri(uri.AbsoluteUri.TrimEnd('/') + "/");
        _http = httpClient; Protocol = protocol; _headers = new(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in headers ?? new Dictionary<string, string>())
        {
            if (name.Equals("authorization", StringComparison.OrdinalIgnoreCase) || name.Equals("x-api-key", StringComparison.OrdinalIgnoreCase) || name.Equals("x-goog-api-key", StringComparison.OrdinalIgnoreCase) || name.Equals("host", StringComparison.OrdinalIgnoreCase)) throw ProviderProtocol.Invalid("credentials must use private credential argument");
            using var check = new HttpRequestMessage();
            try { check.Headers.Add(name, value); }
            catch (Exception e) when (e is FormatException or InvalidOperationException or ArgumentException) { throw ProviderProtocol.Invalid("invalid custom header"); }
            _headers[name] = value;
        }
    }

    public Task<Turn> CallAsync(ProviderCredentials credentials, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools, Action<ProviderProgress>? progress = null, CancellationToken cancellationToken = default)
        => CallWithRetryAsync(credentials, options, history, tools, new ProviderRetryPolicy(), progress, cancellationToken);

    public async Task<Turn> CallWithRetryAsync(ProviderCredentials credentials, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools, ProviderRetryPolicy retryPolicy, Action<ProviderProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        retryPolicy.Validate();
        if (cancellationToken.IsCancellationRequested) throw new ProviderException(ErrorKind.Cancelled, "provider call cancelled");
        var body = ProviderProtocol.BuildRequest(Protocol, options, history, tools).ToJsonString();
        var endpoint = Endpoint(options.Model.Id, options.Stream);
        for (var attempt = 1; ; attempt++)
        {
            try { return await CallAttemptAsync(credentials, endpoint, body, options.Stream, progress, cancellationToken).ConfigureAwait(false); }
            catch (ProviderException error) when (attempt < retryPolicy.MaximumAttempts && error.IsHttpRejection && error.Error.Status is 429 or >= 500 && error.Retryable)
            {
                // Only HTTP-level rejection has a Status. Stream/native errors deliberately have no retry status.
                await DelayAsync(error.Error.RetryAfter, attempt, retryPolicy, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task<Turn> CallAttemptAsync(ProviderCredentials credentials, Uri endpoint, string body, bool stream, Action<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var request = Request(HttpMethod.Post, endpoint, credentials);
            request.Content = new StringContent(body, Encoding.UTF8, "application/json");
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await RejectAsync(response, cancellationToken).ConfigureAwait(false);
            if (!stream)
            {
                var raw = await ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return ProviderProtocol.Parse(Protocol, raw);
            }
            var assembler = new ProviderStreamAssembler(Protocol);
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            var bytes = new byte[8192];
            while (true)
            {
                var count = await source.ReadAsync(bytes, cancellationToken).ConfigureAwait(false);
                if (count == 0) break;
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var item in assembler.Push(bytes.AsSpan(0, count))) progress?.Invoke(item);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return assembler.Finish();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new ProviderException(ErrorKind.Cancelled, "provider call cancelled"); }
        catch (OperationCanceledException) { throw new ProviderException(ErrorKind.Network, "provider connection timed out"); }
        catch (HttpRequestException) { throw new ProviderException(ErrorKind.Network, "provider connection failed"); }
        catch (IOException) { throw new ProviderException(ErrorKind.Network, "provider response interrupted"); }
    }

    public async Task<ModelPage> ListModelsAsync(ProviderCredentials credentials, string? cursor = null, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var endpoint = new Uri(_base, "models");
            if (cursor is not null)
            {
                var name = Protocol switch { Protocol.Responses => "after", Protocol.Messages => "after_id", _ => "pageToken" };
                endpoint = new Uri(endpoint.AbsoluteUri + "?" + name + "=" + Uri.EscapeDataString(cursor));
            }
            using var request = Request(HttpMethod.Get, endpoint, credentials);
            using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            await RejectAsync(response, cancellationToken).ConfigureAwait(false);
            var raw = await ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return ProviderProtocol.ParseModelPage(Protocol, raw);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new ProviderException(ErrorKind.Cancelled, "model listing cancelled"); }
        catch (OperationCanceledException) { throw new ProviderException(ErrorKind.Network, "provider connection timed out"); }
        catch (HttpRequestException) { throw new ProviderException(ErrorKind.Network, "provider connection failed"); }
        catch (IOException) { throw new ProviderException(ErrorKind.Network, "provider response interrupted"); }
    }

    private HttpRequestMessage Request(HttpMethod method, Uri endpoint, ProviderCredentials credentials)
    {
        var request = new HttpRequestMessage(method, endpoint);
        foreach (var (name, value) in _headers) request.Headers.Add(name, value);
        switch (Protocol)
        {
            case Protocol.Responses: request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Key); break;
            case Protocol.Messages:
                request.Headers.Add("x-api-key", credentials.Key);
                if (!request.Headers.Contains("anthropic-version")) request.Headers.Add("anthropic-version", "2023-06-01");
                break;
            case Protocol.Gemini: request.Headers.Add("x-goog-api-key", credentials.Key); break;
        }
        return request;
    }
    private Uri Endpoint(string model, bool stream)
    {
        if (Protocol != Protocol.Gemini) return new Uri(_base, Protocol == Protocol.Responses ? "responses" : "messages");
        var id = model.StartsWith("models/", StringComparison.Ordinal) ? model[7..] : model;
        if (string.IsNullOrEmpty(id) || id is "." or ".." || id.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))) throw ProviderProtocol.Invalid("Gemini model must be a plain model ID or models/ID");
        return new Uri(_base, $"models/{id}:{(stream ? "streamGenerateContent?alt=sse" : "generateContent")}");
    }
    private static bool IsLoopback(Uri uri) => uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || uri.Host is "127.0.0.1" or "[::1]" or "::1";
    private static async Task RejectAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode) return;
        var retry = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
        JsonElement raw = default;
        // Error payloads are used only for classification and are never retained on exceptions.
        try { raw = await ReadJsonAsync(response.Content, cancellationToken).ConfigureAwait(false); }
        catch (ProviderException error) when (error.Kind == ErrorKind.Protocol) { }
        var rejection = ProviderErrors.Classify((ushort)response.StatusCode, raw, retry);
        rejection.IsHttpRejection = true;
        throw rejection;
    }
    private static async Task<JsonElement> ReadJsonAsync(HttpContent content, CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > SseDecoder.MaximumBytes) throw ProviderProtocol.ProtocolError("provider response exceeds limit");
        await using var source = await content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        while (true)
        {
            var length = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (length == 0) break;
            if (length > SseDecoder.MaximumBytes - bytes.Length) throw ProviderProtocol.ProtocolError("provider response exceeds limit");
            bytes.Write(buffer, 0, length);
        }
        try { using var doc = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length)); return doc.RootElement.Clone(); }
        catch (JsonException) { throw ProviderProtocol.ProtocolError("invalid response JSON"); }
    }
    private static async Task DelayAsync(string? retryAfter, int attempt, ProviderRetryPolicy policy, CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(Math.Min(policy.MaximumDelay.TotalMilliseconds, policy.InitialDelay.TotalMilliseconds * Math.Pow(2, attempt - 1)));
        if (int.TryParse(retryAfter, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var seconds) && seconds >= 0) delay = TimeSpan.FromSeconds(seconds);
        else if (DateTimeOffset.TryParse(retryAfter, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.AssumeUniversal, out var date)) delay = date - DateTimeOffset.UtcNow;
        delay = TimeSpan.FromMilliseconds(Math.Clamp(delay.TotalMilliseconds, 0, policy.MaximumDelay.TotalMilliseconds));
        try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
        catch (OperationCanceledException) { throw new ProviderException(ErrorKind.Cancelled, "provider retry cancelled"); }
    }
    public void Dispose() { if (_ownsHttp) _http.Dispose(); }
}
