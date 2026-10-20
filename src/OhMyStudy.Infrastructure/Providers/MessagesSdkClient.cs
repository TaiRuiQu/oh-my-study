using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Anthropic;
using Anthropic.Core;
using Anthropic.Exceptions;
using Anthropic.Models.Messages;
using Anthropic.Models.Models;
using OhMyStudy.Domain;
using RequestOptions = OhMyStudy.Domain.RequestOptions;
using Tool = OhMyStudy.Domain.Tool;

namespace OhMyStudy.Infrastructure.Providers;

/// <summary>
/// Messages transport through the official SDK. Keep its public API when replacing the
/// Anthropic package with TairuiQu.Anthropic.Aot after the user announces its availability.
/// </summary>
internal sealed class MessagesSdkClient(HttpClient http, Uri baseUri, IReadOnlyDictionary<string, string> headers)
{
    private static readonly string[] ReservedKeys =
        ["model", "messages", "max_tokens", "stream", "tools", "thinking", "output_config", "cache_control"];

    public async Task<Turn> CallAsync(ProviderCredentials credentials, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools, Action<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = BuildOptions(options, history, tools);
            using var client = Client(credentials, options.Stream);
            if (!options.Stream)
            {
                var message = await client.Messages.Create(request, cancellationToken).ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
                return ProviderProtocol.Parse(Protocol.Messages, Element(message.RawData));
            }
            var assembler = new ProviderStreamAssembler(Protocol.Messages);
            await foreach (var update in client.Messages.CreateStreaming(request, cancellationToken).ConfigureAwait(false))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (update.Value is not JsonModel model) throw ProviderProtocol.ProtocolError("invalid Messages stream event");
                foreach (var item in assembler.PushMessageEvent(Object(model.RawData))) progress?.Invoke(item);
            }
            cancellationToken.ThrowIfCancellationRequested();
            return assembler.Finish();
        }
        catch (Exception error) { throw Map(error, cancellationToken); }
    }

    public async Task<ModelPage> ListModelsAsync(ProviderCredentials credentials, string? cursor, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var client = Client(credentials, false);
            using var response = await client.Models.WithRawResponse.List(new ModelListParams { AfterID = cursor }, cancellationToken).ConfigureAwait(false);
            // Keep unknown model metadata, and return exactly one page without SDK auto-pagination.
            var raw = await response.ReadAsString(cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(raw);
            return ProviderProtocol.ParseModelPage(Protocol.Messages, document.RootElement);
        }
        catch (Exception error) { throw Map(error, cancellationToken); }
    }

    public static JsonObject BuildBody(RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools)
        => Object(BuildOptions(options, history, tools).RawBodyData);

    private static MessageCreateParams BuildOptions(RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools)
    {
        try { options.Validate(Protocol.Messages, ProviderProtocol.HasImages(Protocol.Messages, history), tools.Count > 0); }
        catch (DomainValidationException error) { throw ProviderProtocol.Invalid(error.Message); }
        if (options.MaxOutputTokens > long.MaxValue) throw ProviderProtocol.Invalid("model and positive token limit are required");
        foreach (var key in options.Extra.Keys)
            if (ReservedKeys.Contains(key, StringComparer.Ordinal)) throw ProviderProtocol.Invalid($"native parameter collision: {key}");

        var request = new MessageCreateParams
        {
            Model = options.Model.Id,
            MaxTokens = (long)options.MaxOutputTokens,
            Messages = history.Select(node => MessageParam.FromRawUnchecked(Properties(node as JsonObject ?? throw ProviderProtocol.Invalid("invalid native context")))).ToArray(),
            Tools = tools.Select(tool => new ToolUnion(Anthropic.Models.Messages.Tool.FromRawUnchecked(Properties(new JsonObject
            {
                ["name"] = tool.Name,
                ["description"] = tool.Description,
                ["input_schema"] = JsonNode.Parse(tool.Schema.GetRawText())
            })))).ToArray(),
            // Automatic prefix caching advances the breakpoint as history grows; default TTL is five minutes.
            CacheControl = new CacheControlEphemeral()
        };
        if (options.Thinking is AdaptiveThinking adaptive)
            request = request with { Thinking = new ThinkingConfigAdaptive(), OutputConfig = OutputConfig.FromRawUnchecked(Properties(new JsonObject { ["effort"] = adaptive.Effort })) };
        else if (options.Thinking is BudgetThinking budget)
            request = request with { Thinking = new ThinkingConfigEnabled { BudgetTokens = (long)budget.Tokens } };
        var body = request.RawBodyData.ToDictionary(p => p.Key, p => p.Value);
        body["stream"] = JsonElement.Parse(options.Stream ? "true" : "false");
        foreach (var (key, value) in options.Extra) body.Add(key, value.Clone());
        return MessageCreateParams.FromRawUnchecked(request.RawHeaderData, request.RawQueryData, body);
    }

    private AnthropicClient Client(ProviderCredentials credentials, bool stream) => new()
    {
        ApiKey = credentials.Key,
        AuthToken = null,
        BaseUrl = baseUri.GetLeftPart(UriPartial.Authority),
        MaxRetries = 0,
        Timeout = stream ? System.Threading.Timeout.InfiniteTimeSpan : TimeSpan.FromSeconds(180),
        HttpClient = new HttpClient(new Transport(http, baseUri, headers)) { Timeout = System.Threading.Timeout.InfiniteTimeSpan }
    };

    private static Dictionary<string, JsonElement> Properties(JsonObject node)
    {
        using var document = JsonDocument.Parse(node.ToJsonString());
        return document.RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
    }
    private static JsonObject Object(IReadOnlyDictionary<string, JsonElement> values)
    {
        var node = new JsonObject();
        foreach (var (key, value) in values) node[key] = JsonNode.Parse(value.GetRawText());
        return node;
    }
    private static JsonElement Element(IReadOnlyDictionary<string, JsonElement> values)
    {
        using var document = JsonDocument.Parse(Object(values).ToJsonString());
        return document.RootElement.Clone();
    }

    private static ProviderException Map(Exception error, CancellationToken cancellationToken)
    {
        if (error is ProviderException provider) return provider;
        if (cancellationToken.IsCancellationRequested) return new(ErrorKind.Cancelled, "provider call cancelled");
        if (error is AnthropicSseException sse)
        {
            var kind = sse.ErrorType switch
            {
                Anthropic.Models.ErrorType.AuthenticationError or Anthropic.Models.ErrorType.PermissionError => ErrorKind.Authentication,
                Anthropic.Models.ErrorType.RateLimitError => ErrorKind.RateLimit,
                Anthropic.Models.ErrorType.BillingError => ErrorKind.Quota,
                Anthropic.Models.ErrorType.OverloadedError or Anthropic.Models.ErrorType.ApiError => ErrorKind.Server,
                Anthropic.Models.ErrorType.TimeoutError => ErrorKind.Network,
                Anthropic.Models.ErrorType.InvalidRequestError or Anthropic.Models.ErrorType.NotFoundError => ErrorKind.InvalidRequest,
                _ => ErrorKind.Protocol
            };
            // SSE errors are never HTTP rejections, even when retryable; never replay partial output.
            return new ProviderException(kind, "provider stream failed");
        }
        return error switch
        {
            OperationCanceledException => new(ErrorKind.Network, "provider connection timed out"),
            HttpRequestException or AnthropicIOException => new(ErrorKind.Network, "provider connection failed"),
            IOException => new(ErrorKind.Network, "provider response interrupted"),
            JsonException => ProviderProtocol.ProtocolError("invalid response JSON"),
            _ => ProviderProtocol.ProtocolError("invalid provider response")
        };
    }

    /// <summary>Preserves the configured endpoint prefix, custom headers and response bounds.
    /// HTTP rejections are sanitized before the SDK can retain their response bodies.</summary>
    private sealed class Transport(HttpClient inner, Uri endpoint, IReadOnlyDictionary<string, string> customHeaders) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw ProviderProtocol.Invalid("missing provider URL");
            var resource = uri.AbsolutePath switch { "/v1/messages" => "messages", "/v1/models" => "models", _ => throw ProviderProtocol.Invalid("unexpected Messages endpoint") };
            request.RequestUri = new Uri(endpoint, resource + uri.Query.Replace("+", "%20", StringComparison.Ordinal));
            foreach (var (name, value) in customHeaders)
            {
                request.Headers.Remove(name);
                request.Headers.Add(name, value);
            }
            // A request cannot pass through two HttpClient instances without a fresh envelope.
            using var forwarded = new HttpRequestMessage(request.Method, request.RequestUri)
            {
                Content = request.Content,
                Version = request.Version,
                VersionPolicy = request.VersionPolicy
            };
            foreach (var header in request.Headers) forwarded.Headers.TryAddWithoutValidation(header.Key, header.Value);
            var response = await inner.SendAsync(forwarded, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            try
            {
                response.Content = new BoundedContent(response.Content);
                if (response.IsSuccessStatusCode) return response;
                JsonElement raw = default;
                try { using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false)); raw = document.RootElement.Clone(); }
                catch (JsonException) { }
                catch (ProviderException error) when (error.Kind == ErrorKind.Protocol) { }
                var retry = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
                var rejection = ProviderErrors.Classify((ushort)response.StatusCode, raw, retry);
                rejection.IsHttpRejection = true;
                throw rejection;
            }
            catch { response.Dispose(); throw; }
        }
    }

    private sealed class BoundedContent : HttpContent
    {
        private readonly HttpContent _inner;
        public BoundedContent(HttpContent inner)
        {
            _inner = inner;
            if (inner.Headers.ContentLength > SseDecoder.MaximumBytes) throw ProviderProtocol.ProtocolError("provider response exceeds limit");
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        protected override bool TryComputeLength(out long length) { length = _inner.Headers.ContentLength ?? 0; return _inner.Headers.ContentLength.HasValue; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            await using var source = await CreateContentReadStreamAsync(cancellationToken).ConfigureAwait(false);
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }
        protected override Task<Stream> CreateContentReadStreamAsync() => CreateContentReadStreamAsync(CancellationToken.None);
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken)
            => new BoundedStream(await _inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        protected override void Dispose(bool disposing) { if (disposing) _inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class BoundedStream(Stream inner) : Stream
    {
        private long _read;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Account(inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Account(inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            => Account(await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        private int Account(int count) { _read += count; if (_read > SseDecoder.MaximumBytes) throw ProviderProtocol.ProtocolError("provider response exceeds limit"); return count; }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
