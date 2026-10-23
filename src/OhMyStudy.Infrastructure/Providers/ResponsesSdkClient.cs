#pragma warning disable OPENAI001 // The SDK marks the Responses client experimental; the wire contract is the stable Responses API.
#pragma warning disable SCME0001 // JsonPatch carries native parameters the typed surface does not model yet.
using System.ClientModel;
using System.ClientModel.Primitives;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using OpenAI.Responses;
using SdkRequestOptions = System.ClientModel.Primitives.RequestOptions;
using RequestOptions = OhMyStudy.Domain.RequestOptions;

namespace OhMyStudy.Infrastructure.Providers;

/// <summary>
/// OpenAI Responses traffic through the official SDK: request shaping, authentication, SSE framing
/// and response models belong to the SDK; this type only maps them onto the application contract.
/// </summary>
internal sealed class ResponsesSdkClient
{
    private static readonly string[] EmittedKeys =
        ["model", "input", "max_output_tokens", "stream", "store", "include", "tools"];

    private readonly Uri _base;
    private readonly IReadOnlyDictionary<string, string> _headers;
    private readonly HttpClientPipelineTransport _transport;
    private readonly bool _openRouter;

    public ResponsesSdkClient(HttpClient http, Uri baseUri, IReadOnlyDictionary<string, string> headers)
    {
        _base = baseUri;
        _headers = headers;
        _transport = new HttpClientPipelineTransport(http);
        _openRouter = OpenRouter.IsEndpoint(baseUri);
    }

    public async Task<Turn> CallAsync(ProviderCredentials credentials, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools, Action<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = BuildOptions(options, history, tools);
            if (_openRouter) ApplyOpenRouterOptions(request, options);
            var client = Client(credentials, options.Stream);
            if (!options.Stream)
            {
                var result = await client.CreateResponseAsync(request, cancellationToken).ConfigureAwait(false);
                return ProviderProtocol.Parse(Protocol.Responses, Read(result.GetRawResponse().Content));
            }
            var assembler = new ResponsesStreamAssembler(progress);
            await foreach (var update in client.CreateResponseStreamingAsync(request, cancellationToken).ConfigureAwait(false))
                assembler.Push(update);
            return assembler.Finish();
        }
        catch (Exception error) { throw Map(error, cancellationToken); }
    }

    public async Task<ModelPage> ListModelsAsync(ProviderCredentials credentials, string? cursor, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var request = new SdkRequestOptions { BufferResponse = true, CancellationToken = cancellationToken };
            if (cursor is not null) request.AddPolicy(new CursorPolicy(cursor), PipelinePosition.PerCall);
            var result = await ModelClient(credentials).GetModelsAsync(request).ConfigureAwait(false);
            return ProviderProtocol.ParseModelPage(Protocol.Responses, Read(result.GetRawResponse().Content));
        }
        catch (Exception error) { throw Map(error, cancellationToken); }
    }

    /// <summary>Wire body for the same options, used by protocol checks and diagnostics.</summary>
    public static JsonObject BuildBody(RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools)
    {
        var request = BuildOptions(options, history, tools);
        var json = ModelReaderWriter.Write(request, ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default).ToString();
        return JsonNode.Parse(json) as JsonObject ?? throw ProviderProtocol.ProtocolError("invalid request JSON");
    }

    private static CreateResponseOptions BuildOptions(RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools)
    {
        try { options.Validate(Protocol.Responses, ProviderProtocol.HasImages(history), tools.Count > 0); }
        catch (DomainValidationException error) { throw ProviderProtocol.Invalid(error.Message); }
        if (options.MaxOutputTokens > int.MaxValue) throw ProviderProtocol.Invalid("model and positive token limit are required");
        var request = new CreateResponseOptions
        {
            Model = options.Model.Id,
            MaxOutputTokenCount = (int)options.MaxOutputTokens,
            StreamingEnabled = options.Stream,
            StoredOutputEnabled = false
        };
        request.Patch.Set("$.stream"u8, options.Stream);
        request.IncludedProperties.Add(IncludedResponseProperty.ReasoningEncryptedContent);
        foreach (var item in Items(history)) request.InputItems.Add(item);
        foreach (var tool in tools)
            request.Tools.Add(ResponseTool.CreateFunctionTool(tool.Name, BinaryData.FromString(tool.Schema.GetRawText()), false, tool.Description));
        if (options.Thinking is ResponsesThinking thinking)
            request.ReasoningOptions = new ResponseReasoningOptions { ReasoningEffortLevel = new ResponseReasoningEffortLevel(thinking.Effort) };
        foreach (var (key, value) in options.Extra)
        {
            if (key == "instructions")
            {
                if (value.ValueKind != JsonValueKind.String) throw ProviderProtocol.Invalid("instructions must be a string");
                request.Instructions = value.GetString();
                continue;
            }
            if (EmittedKeys.Contains(key, StringComparer.Ordinal) || key is "reasoning" or "previous_response_id" or "conversation" or "session_id")
                throw ProviderProtocol.Invalid($"native parameter collision: {key}");
            if (!key.All(c => char.IsAsciiLetterOrDigit(c) || c == '_')) throw ProviderProtocol.Invalid("invalid native parameter name");
            request.Patch.Set(Encoding.UTF8.GetBytes("$." + key), Encoding.UTF8.GetBytes(value.GetRawText()));
        }
        return request;
    }

    /// <summary>
    /// A session id pins every turn of one task to the same provider endpoint, so the next turn reads the
    /// cache the previous turn wrote. Claude caches only a marked prefix. Gemini caches a repeated prefix
    /// implicitly with no write cost; marking blocks there switches to a billed explicit cache instead.
    /// </summary>
    private static void ApplyOpenRouterOptions(CreateResponseOptions request, RequestOptions options)
    {
        var body = JsonNode.Parse(ModelReaderWriter.Write(request, ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default).ToString())!;
        if (options.SessionId is { } session)
        {
            body["session_id"] = session;
            request.Patch.Set("$.session_id"u8, Encoding.UTF8.GetBytes(body["session_id"]!.ToJsonString()));
        }
        if (!OpenRouter.IsClaude(options.Model.Id)) return;
        Breakpoint(body["input"]!.AsArray());
        request.Patch.Set("$.input"u8, Encoding.UTF8.GetBytes(body["input"]!.ToJsonString()));
    }

    /// <summary>Claude accepts four breakpoints: the shared context plus the three most recent tool results.</summary>
    private static void Breakpoint(JsonArray input)
    {
        var blocks = input.OfType<JsonObject>().Select(item => item["content"] as JsonArray ?? item["output"] as JsonArray)
            .Select(parts => parts?.OfType<JsonObject>().LastOrDefault(part => Text(part, "type") == "input_text"))
            .OfType<JsonObject>().ToList();
        if (blocks.Count == 0) return;
        foreach (var block in blocks.Take(1).Concat(blocks.Skip(1).TakeLast(3)))
            block["prompt_cache_breakpoint"] = new JsonObject { ["mode"] = "explicit" };
    }

    /// <summary>Converts stored native items to SDK models; opaque context is preserved byte for byte.</summary>
    private static IReadOnlyList<ResponseItem> Items(JsonArray history)
    {
        var items = new List<ResponseItem>(history.Count);
        foreach (var node in history)
        {
            if (node is not JsonObject item) throw ProviderProtocol.Invalid("invalid native context");
            if (Text(item, "type") is null && Text(item, "role") is not null)
            {
                // Application history stores the initial turn as a bare role message; the SDK needs its kind.
                item = (JsonObject)item.DeepClone();
                item.Insert(0, "type", "message");
            }
            if (Text(item, "type") == "function_call_output" && item["output"] is JsonArray output)
            {
                // The SDK models text output only; a block array (text plus images) travels through the native field.
                var callId = Text(item, "call_id") ?? throw ProviderProtocol.ProtocolError("missing call_id");
                var functionOutput = ResponseItem.CreateFunctionCallOutputItem(callId, "");
                functionOutput.Patch.Set("$.output"u8, Encoding.UTF8.GetBytes(output.ToJsonString()));
                items.Add(functionOutput);
                continue;
            }
            try
            {
                items.Add(ModelReaderWriter.Read<ResponseItem>(BinaryData.FromString(item.ToJsonString()), ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default)
                    ?? throw ProviderProtocol.ProtocolError("invalid native context"));
            }
            catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or NotSupportedException or ArgumentException)
            { throw ProviderProtocol.ProtocolError("invalid native context"); }
        }
        return items;
    }

    private ResponsesClient Client(ProviderCredentials credentials, bool stream)
    {
        var options = new ResponsesClientOptions
        {
            Endpoint = _base,
            Transport = _transport,
            RetryPolicy = new ClientRetryPolicy(0),
            // Non-streaming replies are bounded end to end; streams stay open until the caller cancels.
            NetworkTimeout = stream ? null : TimeSpan.FromSeconds(180)
        };
        options.AddPolicy(new HeaderPolicy(_headers), PipelinePosition.PerCall);
        options.AddPolicy(new ResponseGuardPolicy(stream), PipelinePosition.PerCall);
        return new ResponsesClient(new ApiKeyCredential(credentials.Key), options);
    }

    private OpenAI.Models.OpenAIModelClient ModelClient(ProviderCredentials credentials)
    {
        var options = new OpenAI.OpenAIClientOptions
        {
            Endpoint = _base,
            Transport = _transport,
            RetryPolicy = new ClientRetryPolicy(0)
        };
        options.AddPolicy(new HeaderPolicy(_headers), PipelinePosition.PerCall);
        options.AddPolicy(new ResponseGuardPolicy(streaming: false), PipelinePosition.PerCall);
        return new OpenAI.Models.OpenAIModelClient(new ApiKeyCredential(credentials.Key), options);
    }

    private static JsonElement Read(BinaryData content)
    {
        var memory = content.ToMemory();
        if (memory.Length > SseDecoder.MaximumBytes) throw ProviderProtocol.ProtocolError("provider response exceeds limit");
        try { using var document = JsonDocument.Parse(memory); return document.RootElement.Clone(); }
        catch (JsonException) { throw ProviderProtocol.ProtocolError("invalid response JSON"); }
    }

    private static ProviderException Map(Exception error, CancellationToken cancellationToken)
    {
        if (error is ProviderException provider) return provider;
        if (cancellationToken.IsCancellationRequested) return new(ErrorKind.Cancelled, "provider call cancelled");
        return error switch
        {
            ClientResultException { Status: 0 } => new(ErrorKind.Network, "provider connection failed"),
            ClientResultException rejection => Reject(rejection),
            OperationCanceledException => new(ErrorKind.Network, "provider connection timed out"),
            IOException => new(ErrorKind.Network, "provider response interrupted"),
            JsonException => ProviderProtocol.ProtocolError("invalid response JSON"),
            _ => ProviderProtocol.ProtocolError("invalid provider response")
        };
    }

    private static ProviderException Reject(ClientResultException error)
    {
        PipelineResponse? response = null;
        try { response = error.GetRawResponse(); }
        catch (InvalidOperationException) { }
        JsonElement body = default;
        string? retryAfter = null;
        if (response is not null)
        {
            // Error payloads are used only for classification and are never retained on exceptions.
            try { using var document = JsonDocument.Parse(response.Content.ToMemory()); body = document.RootElement.Clone(); }
            catch (JsonException) { }
            catch (ArgumentNullException) { }
            response.Headers.TryGetValue("Retry-After", out retryAfter);
        }
        var rejection = ProviderErrors.Classify((ushort)Math.Clamp(error.Status, 0, ushort.MaxValue), body, retryAfter);
        rejection.IsHttpRejection = true;
        return rejection;
    }

    private static string? Text(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private sealed class HeaderPolicy(IReadOnlyDictionary<string, string> headers) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            ProcessNext(message, pipeline, currentIndex);
        }
        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }
        private void Apply(PipelineMessage message)
        {
            foreach (var (name, value) in headers) message.Request.Headers.Set(name, value);
        }
    }

    /// <summary>Rejects oversized bodies. Streaming bodies are also wrapped so cancellation releases a pending read.</summary>
    private sealed class ResponseGuardPolicy(bool streaming) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            ProcessNext(message, pipeline, currentIndex);
            Apply(message);
        }
        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
            Apply(message);
        }
        private void Apply(PipelineMessage message)
        {
            if (message.Response is not { } response) return;
            if (response.Headers.TryGetValue("Content-Length", out var length) && long.TryParse(length, out var size) && size > SseDecoder.MaximumBytes)
                throw ProviderProtocol.ProtocolError("provider response exceeds limit");
            // A buffered non-streaming body arrives complete; wrapping it would detach the SDK's buffered content.
            if (streaming && response.ContentStream is { } stream) response.ContentStream = new GuardStream(stream, message.CancellationToken);
        }
    }

    private sealed class GuardStream : Stream
    {
        private readonly Stream _inner;
        private readonly CancellationTokenRegistration _registration;
        private long _read;
        public GuardStream(Stream inner, CancellationToken cancellationToken)
        {
            _inner = inner;
            // The SDK does not observe cancellation while a body read is pending; closing the body releases it.
            if (cancellationToken.CanBeCanceled)
                _registration = cancellationToken.Register(static state => { try { ((Stream)state!).Dispose(); } catch (ObjectDisposedException) { } catch (IOException) { } }, inner);
        }
        public override bool CanRead => true;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position { get => _read; set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => Account(_inner.Read(buffer, offset, count));
        public override int Read(Span<byte> buffer) => Account(_inner.Read(buffer));
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => Account(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override long Seek(long offset, SeekOrigin origin) => _inner.Seek(offset, origin);
        private int Account(int count)
        {
            _read += count;
            if (_read > SseDecoder.MaximumBytes) throw ProviderProtocol.ProtocolError("provider response exceeds limit");
            return count;
        }
        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { _registration.Dispose(); _inner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class CursorPolicy(string cursor) : PipelinePolicy
    {
        public override void Process(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            ProcessNext(message, pipeline, currentIndex);
        }
        public override async ValueTask ProcessAsync(PipelineMessage message, IReadOnlyList<PipelinePolicy> pipeline, int currentIndex)
        {
            Apply(message);
            await ProcessNextAsync(message, pipeline, currentIndex).ConfigureAwait(false);
        }
        private void Apply(PipelineMessage message)
        {
            var uri = message.Request.Uri ?? throw ProviderProtocol.ProtocolError("missing request address");
            message.Request.Uri = new Uri(uri.AbsoluteUri + (uri.Query.Length == 0 ? "?" : "&") + "after=" + Uri.EscapeDataString(cursor));
        }
    }

    /// <summary>Maps SDK stream updates onto the application turn; a terminal response is mandatory.</summary>
    private sealed class ResponsesStreamAssembler(Action<ProviderProgress>? progress)
    {
        private ResponseResult? _terminal;
        public void Push(StreamingResponseUpdate update)
        {
            switch (update)
            {
                case StreamingResponseOutputTextDeltaUpdate { Delta: { } text }: progress?.Invoke(new(Text: text)); break;
                case StreamingResponseOutputItemAddedUpdate { Item: FunctionCallResponseItem call }: progress?.Invoke(new(ToolName: call.FunctionName ?? "")); break;
                case StreamingResponseRefusalDeltaUpdate or StreamingResponseRefusalDoneUpdate: throw new ProviderException(ErrorKind.Refusal, "provider declined the request");
                case StreamingResponseErrorUpdate error: throw ProviderErrors.Classify(400, Element(new JsonObject { ["error"] = new JsonObject { ["code"] = error.Code, ["message"] = error.Message } }));
                case StreamingResponseFailedUpdate failed: throw ProviderErrors.Classify(400, Element(Failure(failed.Response)));
                case StreamingResponseIncompleteUpdate: throw ProviderProtocol.Incomplete("Responses stream incomplete");
                case StreamingResponseCompletedUpdate completed: _terminal = completed.Response ?? throw ProviderProtocol.ProtocolError("missing response"); break;
            }
        }
        public Turn Finish()
        {
            if (_terminal is null) throw ProviderProtocol.Incomplete("stream ended before terminal event");
            return ProviderProtocol.Parse(Protocol.Responses, Element(JsonNode.Parse(ModelReaderWriter.Write(_terminal, ModelReaderWriterOptions.Json, OpenAI.OpenAIContext.Default).ToString())!));
        }
        private static JsonObject Failure(ResponseResult? response) => new()
        {
            ["error"] = new JsonObject
            {
                ["code"] = response?.Error?.Code.ToString(),
                ["message"] = response?.Error?.Message
            }
        };
        private static JsonElement Element(JsonNode node) { using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone(); }
    }
}
