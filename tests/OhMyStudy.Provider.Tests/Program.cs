using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

[assembly: SupportedOSPlatform("windows10.0.19041")]

// All payloads and credentials in this runner are synthetic and stay inside fake handlers.
var fixtures = JsonNode.Parse(await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "round-trips.synthetic.json")))!;
var completed = 0;
await Run("native requests, images, opaque tool context and usage", NativeRoundTrips);
await Run("native thinking and collision checks", Thinking);
await Run("native image capability checks", CapabilityChecks);
await Run("incremental Unicode SSE for all protocols", Streams);
await Run("invalid streaming states, limits and refusals", InvalidStreams);
await Run("models preserve metadata and unknown capabilities", Models);
await Run("native HTTP endpoints, headers and streaming bodies", HttpFraming);
await Run("sanitized errors and bounded explicit retries", ErrorsAndRetries);
await Run("cancellation and ambiguous failures never replay", Cancellation);
await Run("response bounds and URL/header validation", Bounds);
Console.WriteLine($"Provider protocol regression: {completed} scenarios passed (synthetic, no supplier network).");

async Task Run(string name, Func<Task> test)
{
    try { await test(); completed++; Console.WriteLine($"PASS {name}"); }
    catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error.GetType().Name}: {error.Message}"); Environment.ExitCode = 1; throw; }
}
static void Check(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
static JsonElement Element(JsonNode node) { using var doc = JsonDocument.Parse(node.ToJsonString()); return doc.RootElement.Clone(); }
static JsonElement Json(string text) { using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone(); }
static string Key(Protocol p) => p.ToString().ToLowerInvariant();
static RequestOptions Options(bool stream = false) => new() { Model = Model.Manual("manual-model"), MaxOutputTokens = 4096, Stream = stream, AllowUnknownCapabilities = true, Extra = [] };
static Image Image() => new() { Mime = "image/png", Base64 = "SYNTHETIC_IMAGE" };
static Tool Tool() => new() { Name = "read_image", Description = "Read source page", Schema = Json("""{"type":"object","properties":{"page":{"type":"integer"}},"required":["page"]}""") };
static string Sse(JsonNode value) => $"data: {value.ToJsonString(new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping })}\r\n\r\n";
static ProviderException Throws(Action action, ErrorKind? kind = null)
{
    try { action(); } catch (ProviderException error) { if (kind is { } expected) Check(error.Kind == expected, "error kind"); return error; }
    throw new InvalidOperationException("expected provider rejection");
}
static async Task<ProviderException> ThrowsAsync(Func<Task> action, ErrorKind? kind = null)
{
    try { await action(); } catch (ProviderException error) { if (kind is { } expected) Check(error.Kind == expected, "async error kind"); return error; }
    throw new InvalidOperationException("expected provider rejection");
}

Task NativeRoundTrips()
{
    foreach (var protocol in Enum.GetValues<Protocol>())
    {
        var native = fixtures[Key(protocol)]!["tool"]!;
        var turn = ProviderProtocol.Parse(protocol, Element(native));
        Check(turn.Calls.Count == 1 && turn.Calls[0].Arguments.GetProperty("page").GetInt32() == 1, "native tool arguments");
        Check(turn.Usage.Input == 100 && turn.Usage.Output == 30 && turn.Usage.Total == 130, "usage normalization");
        Check(ProviderProtocol.EstimateCost(turn.Usage, 1m, 2m) == 0.00016m, "configured rate estimate");
        Check(ProviderProtocol.EstimateCost(turn.Usage, null, 2m) is null, "missing rates stay unknown");
        var history = ProviderProtocol.Initial(protocol, "识别整卷😀", [Image()]);
        var snapshot = history.ToJsonString();
        Throws(() => ProviderProtocol.ContinueWith(protocol, history, turn, []), ErrorKind.InvalidRequest);
        Check(snapshot == history.ToJsonString(), "invalid results leave history unchanged");
        var result = new ToolResult { Call = turn.Calls[0], Text = "page 1", Images = [Image()], IsError = false };
        Throws(() => ProviderProtocol.ContinueWith(protocol, history, turn, [result with { Call = result.Call with { Arguments = Json("{\"page\":2}") } }]), ErrorKind.InvalidRequest);
        ProviderProtocol.ContinueWith(protocol, history, turn, [result]);
        var body = ProviderProtocol.BuildRequest(protocol, Options(), history, [Tool()]);
        switch (protocol)
        {
            case Protocol.Responses:
                Check(JsonNode.DeepEquals(body["input"]![1], native["output"]![0]), "opaque Responses reasoning preserved");
                Check(body["input"]![3]!["output"]![1]!["type"]!.GetValue<string>() == "input_image", "Responses result image");
                Check(body["store"]!.GetValue<bool>() == false && body["include"]![0]!.GetValue<string>() == "reasoning.encrypted_content", "stateless native context");
                break;
            case Protocol.Messages:
                Check(JsonNode.DeepEquals(body["messages"]![1], JsonNode.Parse(turn.Context.GetRawText())), "Messages signature and redacted block preserved");
                Check(body["messages"]![2]!["content"]![0]!["content"]![1]!["type"]!.GetValue<string>() == "image", "Messages result image");
                break;
            case Protocol.Gemini:
                Check(JsonNode.DeepEquals(body["contents"]![1], native["candidates"]![0]!["content"]), "Gemini thought signature preserved");
                Check(body["contents"]![2]!["parts"]![0]!["functionResponse"]!["parts"]![0]!["inlineData"]!["mimeType"]!.GetValue<string>() == "image/png", "Gemini result image");
                Check(body["model"] is null && body["generationConfig"]!["candidateCount"]!.GetValue<int>() == 1, "Gemini native framing");
                break;
        }
        var final = ProviderProtocol.Parse(protocol, Element(fixtures[Key(protocol)]!["final"]!));
        Check(final.Text == "完成。" && final.Calls.Count == 0, "final text");
    }
    return Task.CompletedTask;
}

Task Thinking()
{
    foreach (var (protocol, thinking, field, expected) in new (Protocol, Thinking, string, string)[]
    {
        (Protocol.Responses, new ResponsesThinking { Effort = "medium" }, "reasoning", "medium"),
        (Protocol.Messages, new AdaptiveThinking { Effort = "high" }, "output_config", "high"),
        (Protocol.Messages, new BudgetThinking { Tokens = 2048 }, "thinking", "2048"),
        (Protocol.Gemini, new GeminiLevelThinking { Level = "HIGH" }, "generationConfig", "HIGH"),
        (Protocol.Gemini, new GeminiBudgetThinking { Tokens = -1 }, "generationConfig", "-1")
    })
    {
        var options = Options() with { Thinking = thinking };
        var body = ProviderProtocol.BuildRequest(protocol, options, [], []);
        Check(body[field]!.ToJsonString().Contains(expected, StringComparison.Ordinal), "native thinking parameter");
        Throws(() => ProviderProtocol.BuildRequest(protocol, options with { AllowUnknownCapabilities = false }, [], []), ErrorKind.InvalidRequest);
        var wrong = protocol == Protocol.Responses ? Protocol.Messages : Protocol.Responses;
        Throws(() => ProviderProtocol.BuildRequest(wrong, options, [], []), ErrorKind.InvalidRequest);
    }
    Throws(() => ProviderProtocol.BuildRequest(Protocol.Messages, Options() with { Thinking = new BudgetThinking { Tokens = 1023 } }, [], []));
    Throws(() => ProviderProtocol.BuildRequest(Protocol.Messages, Options() with { Thinking = new BudgetThinking { Tokens = 4096 } }, [], []));
    Throws(() => ProviderProtocol.BuildRequest(Protocol.Gemini, Options() with { Thinking = new GeminiBudgetThinking { Tokens = -2 } }, [], []));
    foreach (var (protocol, field) in new[] { (Protocol.Responses, "reasoning"), (Protocol.Messages, "output_config"), (Protocol.Gemini, "contents") })
        Throws(() => ProviderProtocol.BuildRequest(protocol, Options() with { Extra = new() { [field] = Json("{}") } }, [], []));
    var merged = ProviderProtocol.BuildRequest(Protocol.Gemini, Options() with { Extra = new() { ["generationConfig"] = Json("{\"temperature\":0.2}") } }, [], []);
    Check(merged["generationConfig"]!["maxOutputTokens"]!.GetValue<ulong>() == 4096 && merged["generationConfig"]!["temperature"]!.GetValue<double>() == 0.2, "Gemini deliberate merge");
    foreach (var extra in new[] { "{\"thinkingConfig\":{}}", "{\"maxOutputTokens\":1}", "{\"candidateCount\":2}", "[]" })
        Throws(() => ProviderProtocol.BuildRequest(Protocol.Gemini, Options() with { Extra = new() { ["generationConfig"] = Json(extra) } }, [], []));
    return Task.CompletedTask;
}

Task CapabilityChecks()
{
    foreach (var protocol in Enum.GetValues<Protocol>())
    {
        var denied = Options() with { Model = Model.Manual("named-vision-tools-reasoning") with { Capabilities = Capabilities.Unknown with { Vision = false } } };
        ProviderProtocol.BuildRequest(protocol, denied, ProviderProtocol.Initial(protocol, "input_image image inlineData", []), []);
        Throws(() => ProviderProtocol.BuildRequest(protocol, denied, ProviderProtocol.Initial(protocol, "x", [Image()]), []));
        Throws(() => ProviderProtocol.BuildRequest(protocol, Options() with { AllowUnknownCapabilities = false }, [], [Tool()]));
        var turn = ProviderProtocol.Parse(protocol, Element(fixtures[Key(protocol)]!["tool"]!));
        var history = ProviderProtocol.Initial(protocol, "x", []);
        ProviderProtocol.ContinueWith(protocol, history, turn, [new ToolResult { Call = turn.Calls[0], Text = "x", Images = [Image()], IsError = false }]);
        Throws(() => ProviderProtocol.BuildRequest(protocol, denied, history, []));
    }
    // Arbitrary function argument objects are not image content.
    foreach (var (protocol, history) in new[]
    {
        (Protocol.Responses, "[{\"type\":\"function_call\",\"arguments\":\"input_image\"}]"),
        (Protocol.Messages, "[{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"input\":{\"type\":\"image\"}}]}]"),
        (Protocol.Gemini, "[{\"role\":\"model\",\"parts\":[{\"functionCall\":{\"args\":{\"inlineData\":{\"mimeType\":\"image/png\"}}}}]}]")
    }) ProviderProtocol.BuildRequest(protocol, Options() with { Model = Model.Manual("manual") with { Capabilities = Capabilities.Unknown with { Vision = false } } }, (JsonArray)JsonNode.Parse(history)!, []);
    return Task.CompletedTask;
}

async Task Streams()
{
    foreach (var protocol in Enum.GetValues<Protocol>())
    {
        var native = fixtures[Key(protocol)]!["tool"]!;
        var data = protocol switch
        {
            Protocol.Responses => ": synthetic\r\n" + Sse(JsonNode.Parse("{\"type\":\"response.output_text.delta\",\"delta\":\"中文😀\"}")!) + Sse(new JsonObject { ["type"] = "response.completed", ["response"] = native.DeepClone() }),
            Protocol.Messages => await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "messages.synthetic.sse")),
            _ => Sse(native) + Sse(new JsonObject { ["usageMetadata"] = native["usageMetadata"]!.DeepClone() })
        };
        var bytes = Encoding.UTF8.GetBytes(data);
        foreach (var size in new[] { 1, 2, 7, 31, bytes.Length })
        {
            var assembler = new ProviderStreamAssembler(protocol);
            var visible = new List<ProviderProgress>();
            for (var start = 0; start < bytes.Length; start += size) visible.AddRange(assembler.Push(bytes.AsSpan(start, Math.Min(size, bytes.Length - start))));
            var result = assembler.Finish();
            var expected = ProviderProtocol.Parse(protocol, Element(native));
            Check(JsonElement.DeepEquals(result.Context, expected.Context), "stream native context equality");
            Check(result.Usage.Input == expected.Usage.Input && result.Usage.Output == expected.Usage.Output && result.Usage.Total == expected.Usage.Total, "stream usage equality");
            Check(!string.Join("", visible.Select(v => v.Text + v.ToolName)).Contains("SIGNATURE", StringComparison.Ordinal), "progress excludes opaque context");
        }
    }
    var decoder = new SseDecoder();
    var framed = decoder.Push(Encoding.UTF8.GetBytes("\uFEFFevent: sample\rdata: 第一行\rdata: 第二行\r\r"));
    Check(framed.Count == 1 && framed[0].Data == "第一行\n第二行" && framed[0].Event == "sample", "BOM, CR and multiline SSE");
    decoder.Finish();
}

Task InvalidStreams()
{
    foreach (var protocol in Enum.GetValues<Protocol>())
    {
        var assembler = new ProviderStreamAssembler(protocol);
        assembler.Push(Encoding.UTF8.GetBytes("data: {\"type\":\"ping\"}\n\n"));
        Throws(() => assembler.Finish());
    }
    var truncated = new ProviderStreamAssembler(Protocol.Responses);
    truncated.Push(Encoding.UTF8.GetBytes("data: {"));
    Throws(() => truncated.Finish(), ErrorKind.Incomplete);
    Throws(() => new SseDecoder().Push([0xff, (byte)'\n']), ErrorKind.Protocol);
    Throws(() => new ProviderStreamAssembler(Protocol.Responses).Push(new byte[SseDecoder.MaximumBytes + 1]), ErrorKind.Protocol);
    var block = JsonNode.Parse("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}")!;
    Throws(() => new ProviderStreamAssembler(Protocol.Messages).Push(Encoding.UTF8.GetBytes(Sse(block))), ErrorKind.Protocol);
    var messages = new ProviderStreamAssembler(Protocol.Messages);
    messages.Push(Encoding.UTF8.GetBytes(Sse(JsonNode.Parse("{\"type\":\"message_start\",\"message\":{\"content\":[]}}")!)));
    messages.Push(Encoding.UTF8.GetBytes(Sse(block)));
    Throws(() => messages.Push(Encoding.UTF8.GetBytes(Sse(block))), ErrorKind.Protocol);
    Throws(() => messages.Push(Encoding.UTF8.GetBytes("data: {\"type\":\"message_stop\"}\n\n")), ErrorKind.Protocol);
    foreach (var type in new[] { "response.refusal.delta", "response.refusal.done" })
        Throws(() => new ProviderStreamAssembler(Protocol.Responses).Push(Encoding.UTF8.GetBytes(Sse(new JsonObject { ["type"] = type }))), ErrorKind.Refusal);
    Throws(() => ProviderProtocol.Parse(Protocol.Responses, Json("{\"status\":\"completed\",\"output\":[{\"type\":\"message\",\"content\":[{\"type\":\"refusal\"}]}]}")), ErrorKind.Refusal);
    var gemini = fixtures["gemini"]!["final"]!.DeepClone();
    gemini["candidates"]![0]!["finishReason"] = "MAX_TOKENS";
    Throws(() => ProviderProtocol.Parse(Protocol.Gemini, Element(gemini)), ErrorKind.Incomplete);
    return Task.CompletedTask;
}

Task Models()
{
    foreach (var protocol in new[] { Protocol.Responses, Protocol.Gemini })
    {
        var page = ProviderProtocol.ParseModelPage(protocol, protocol == Protocol.Gemini ? Json("{\"models\":[{\"name\":\"models/vision-thinking-tools\"}],\"nextPageToken\":\"two\"}") : Json("{\"data\":[{\"id\":\"vision-thinking-tools\"}]}"));
        Check(page.Models[0].Capabilities.Vision is null && page.Models[0].Capabilities.Tools is null && page.Models[0].Capabilities.Thinking.Count == 0, "names do not establish capabilities");
    }
    var messages = ProviderProtocol.ParseModelPage(Protocol.Messages, Json("""{"data":[{"id":"manual","capabilities":{"image_input":{"supported":true},"tool_use":{"supported":false},"thinking":{"types":{"enabled":{"supported":true},"adaptive":{"supported":true}}},"effort":{"high":{"supported":true},"low":{"supported":false}}}}],"has_more":true,"last_id":"manual"}"""));
    var cap = messages.Models[0].Capabilities;
    Check(cap.Vision == true && cap.Tools == false && cap.MessagesBudget == true && cap.Thinking.SequenceEqual([new AdaptiveThinking { Effort = "high" }]) && cap.Source == "provider_metadata", "exact API metadata only");
    Check(messages.Next == "manual", "Messages cursor");
    Throws(() => ProviderProtocol.ParseModelPage(Protocol.Messages, Json("{\"data\":[],\"has_more\":true}")), ErrorKind.Protocol);
    Throws(() => ProviderProtocol.ParseModelPage(Protocol.Gemini, Json("{}")), ErrorKind.Protocol);
    return Task.CompletedTask;
}

async Task HttpFraming()
{
    foreach (var protocol in Enum.GetValues<Protocol>())
    {
        foreach (var stream in new[] { false, true })
        {
            var calls = 0;
            using var http = new HttpClient(new FakeHandler(async (request, token) =>
            {
                calls++;
                var expectedEndpoint = protocol switch { Protocol.Responses => "responses", Protocol.Messages => "messages", _ => "models/manual-model:" + (stream ? "streamGenerateContent?alt=sse" : "generateContent") };
                if (request.Method == HttpMethod.Post)
                {
                    Check(request.RequestUri!.PathAndQuery == "/v1/" + expectedEndpoint, "native generation endpoint");
                    var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
                    Check(body[protocol switch { Protocol.Responses => "input", Protocol.Messages => "messages", _ => "contents" }] is JsonArray, "native request body");
                    if (protocol != Protocol.Gemini) Check(body["stream"]!.GetValue<bool>() == stream, "native stream field");
                }
                else Check(request.RequestUri!.PathAndQuery == "/v1/models?" + (protocol switch { Protocol.Responses => "after", Protocol.Messages => "after_id", _ => "pageToken" }) + "=next%20token", "encoded model cursor");
                Check(protocol switch
                {
                    Protocol.Responses => request.Headers.Authorization?.Scheme == "Bearer" && request.Headers.Authorization.Parameter == "synthetic-key",
                    Protocol.Messages => request.Headers.GetValues("x-api-key").Single() == "synthetic-key" && request.Headers.GetValues("anthropic-version").Single() == "2023-06-01",
                    _ => request.Headers.GetValues("x-goog-api-key").Single() == "synthetic-key"
                }, "native private auth header");
                Check(!request.RequestUri!.Query.Contains("key", StringComparison.Ordinal), "credential absent from URL");
                if (request.Method == HttpMethod.Get) return Response(protocol == Protocol.Gemini ? "{\"models\":[]}" : "{\"data\":[]}");
                var final = fixtures[Key(protocol)]!["final"]!;
                var data = !stream ? final.ToJsonString() : FinalStream(protocol, final);
                return stream ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new ChunkStream(Encoding.UTF8.GetBytes(data), 1)) } : Response(data);
            }));
            using var client = new ProviderClient(http, protocol, "http://127.0.0.1/v1");
            var credentials = new ProviderCredentials("synthetic-key");
            var visible = new StringBuilder();
            var turn = await client.CallAsync(credentials, Options(stream), ProviderProtocol.Initial(protocol, "x", []), [], p => visible.Append(p.Text));
            Check(turn.Text == "完成。" && (!stream || visible.ToString() == "完成。"), "streamed public output");
            await client.ListModelsAsync(credentials, "next token");
            Check(calls == 2, "one attempt per explicit operation");
        }
    }
}
static string FinalStream(Protocol protocol, JsonNode final) => protocol switch
{
    Protocol.Responses => Sse(new JsonObject { ["type"] = "response.output_text.delta", ["delta"] = "完成。" }) + Sse(new JsonObject { ["type"] = "response.completed", ["response"] = final.DeepClone() }),
    Protocol.Gemini => Sse(final),
    _ => Sse(JsonNode.Parse("{\"type\":\"message_start\",\"message\":{\"content\":[],\"usage\":{\"input_tokens\":100}}}")!) + Sse(JsonNode.Parse("{\"type\":\"content_block_start\",\"index\":0,\"content_block\":{\"type\":\"text\",\"text\":\"\"}}")!) + Sse(JsonNode.Parse("{\"type\":\"content_block_delta\",\"index\":0,\"delta\":{\"type\":\"text_delta\",\"text\":\"完成。\"}}")!) + "data: {\"type\":\"content_block_stop\",\"index\":0}\n\n" + "data: {\"type\":\"message_delta\",\"delta\":{\"stop_reason\":\"end_turn\"},\"usage\":{\"output_tokens\":20}}\n\n" + "data: {\"type\":\"message_stop\"}\n\n"
};

async Task ErrorsAndRetries()
{
    foreach (var (status, body, kind, retry) in new[]
    {
        (401, "", ErrorKind.Authentication, false), (403, "not json", ErrorKind.Authentication, false),
        (429, "{\"error\":{\"message\":\"SYNTHETIC_SECRET_ECHO\"}}", ErrorKind.RateLimit, true),
        (429, "{\"error\":{\"code\":\"insufficient_quota\"}}", ErrorKind.Quota, false),
        (400, "{\"error\":{\"message\":\"unknown parameter\"}}", ErrorKind.UnsupportedParameter, false),
        (503, "<html>SYNTHETIC_SECRET_ECHO</html>", ErrorKind.Server, true)
    })
    {
        var attempts = 0;
        using var http = new HttpClient(new FakeHandler((_, _) => { attempts++; var response = Response(body, status); response.Headers.TryAddWithoutValidation("Retry-After", "7"); return Task.FromResult(response); }));
        using var client = new ProviderClient(http, Protocol.Responses, "https://example.invalid/v1");
        var error = await ThrowsAsync(() => client.CallAsync(new("synthetic-key"), Options(), [], []), kind);
        Check(error.Error.Status == status && error.Error.RetryAfter == "7" && error.Retryable == retry && attempts == 1, "status and retry classification");
        Check(!error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal), "sanitized errors");
        await ThrowsAsync(() => client.ListModelsAsync(new("synthetic-key")), kind);
    }
    var count = 0;
    using var retryHttp = new HttpClient(new FakeHandler((_, _) => { count++; return Task.FromResult(count < 3 ? Response("{}", 429) : Response(fixtures["responses"]!["final"]!.ToJsonString())); }));
    using var retryClient = new ProviderClient(retryHttp, Protocol.Responses, "https://example.invalid/v1");
    var retryPolicy = new ProviderRetryPolicy { MaximumAttempts = 3, InitialDelay = TimeSpan.Zero, MaximumDelay = TimeSpan.Zero };
    var final = await retryClient.CallWithRetryAsync(new("synthetic-key"), Options(), [], [], retryPolicy);
    Check(count == 3 && final.Text == "完成。", "explicit bounded retry");
    count = 0;
    using var alwaysHttp = new HttpClient(new FakeHandler((_, _) => { count++; return Task.FromResult(Response("{}", 503)); }));
    using var alwaysClient = new ProviderClient(alwaysHttp, Protocol.Responses, "https://example.invalid/v1");
    await ThrowsAsync(() => alwaysClient.CallWithRetryAsync(new("synthetic-key"), Options(), [], [], retryPolicy), ErrorKind.Server);
    Check(count == 3, "retry stops at configured bound");
    count = 0;
    using var nativeHttp = new HttpClient(new FakeHandler((_, _) => { count++; return Task.FromResult(Response("data: {\"error\":{\"code\":503}}\n\n")); }));
    using var nativeClient = new ProviderClient(nativeHttp, Protocol.Gemini, "https://example.invalid/v1");
    await ThrowsAsync(() => nativeClient.CallWithRetryAsync(new("synthetic-key"), Options(true), [], [], retryPolicy), ErrorKind.Server);
    Check(count == 1, "native stream failures never replay");
}

async Task Cancellation()
{
    var attempts = 0;
    using var networkHttp = new HttpClient(new FakeHandler((_, _) => { attempts++; throw new HttpRequestException("SYNTHETIC_SECRET_ECHO"); }));
    using var networkClient = new ProviderClient(networkHttp, Protocol.Responses, "https://example.invalid/v1");
    var policy = new ProviderRetryPolicy { MaximumAttempts = 4, InitialDelay = TimeSpan.Zero, MaximumDelay = TimeSpan.Zero };
    var error = await ThrowsAsync(() => networkClient.CallWithRetryAsync(new("synthetic-key"), Options(), [], [], policy), ErrorKind.Network);
    Check(attempts == 1 && !error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal), "ambiguous network failures never replay");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    await ThrowsAsync(() => networkClient.CallAsync(new("synthetic-key"), Options(), [], [], cancellationToken: cancelled.Token), ErrorKind.Cancelled);
    Check(attempts == 1, "pre-cancelled call sends nothing");
    foreach (var stream in new[] { false, true })
    {
        using var cancellation = new CancellationTokenSource();
        var hanging = new HangingStream();
        using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(hanging) })));
        using var client = new ProviderClient(http, Protocol.Responses, "https://example.invalid/v1");
        var pending = client.CallAsync(new("synthetic-key"), Options(stream), [], [], cancellationToken: cancellation.Token);
        await hanging.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await ThrowsAsync(() => pending, ErrorKind.Cancelled);
        Check(hanging.Disposed, "cancelled body disposed");
    }
    using var delayCancel = new CancellationTokenSource();
    using var retryHttp = new HttpClient(new FakeHandler((_, _) => { delayCancel.Cancel(); return Task.FromResult(Response("{}", 429)); }));
    using var retryClient = new ProviderClient(retryHttp, Protocol.Responses, "https://example.invalid/v1");
    await ThrowsAsync(() => retryClient.CallWithRetryAsync(new("synthetic-key"), Options(), [], [], new ProviderRetryPolicy { MaximumAttempts = 2 }, cancellationToken: delayCancel.Token), ErrorKind.Cancelled);
}

async Task Bounds()
{
    foreach (var uri in new[] { "http://example.invalid/v1", "https://key@example.invalid/v1", "https://example.invalid/v1?key=x", "https://example.invalid/v1#x", "file:///tmp/" })
        Throws(() => new ProviderClient(Protocol.Responses, uri), ErrorKind.InvalidRequest);
    foreach (var header in new[] { "Authorization", "X-Api-Key", "X-Goog-Api-Key", "Host" })
        Throws(() => new ProviderClient(Protocol.Responses, "https://example.invalid/v1", new Dictionary<string, string> { [header] = "synthetic-key" }));
    Throws(() => new ProviderCredentials("key\r\nInjected: value"));
    Check(!new ProviderCredentials("synthetic-key").ToString().Contains("synthetic-key", StringComparison.Ordinal), "credential diagnostic redaction");
    var attempts = 0;
    using var http = new HttpClient(new FakeHandler((_, _) => { attempts++; var response = Response("{}"); response.Content.Headers.ContentLength = SseDecoder.MaximumBytes + 1L; return Task.FromResult(response); }));
    using var client = new ProviderClient(http, Protocol.Gemini, "https://example.invalid/v1");
    foreach (var id in new[] { "..", "../escape", "model?key=x", "models/" })
        await ThrowsAsync(() => client.CallAsync(new("synthetic-key"), Options() with { Model = Model.Manual(id) }, [], []), ErrorKind.InvalidRequest);
    Check(attempts == 0, "invalid model cannot send");
    await ThrowsAsync(() => client.CallAsync(new("synthetic-key"), Options(), [], []), ErrorKind.Protocol);
    Check(attempts == 1, "bounded successful response");
}
static HttpResponseMessage Response(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

sealed class FakeHandler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => send(request, cancellationToken);
}
sealed class ChunkStream(byte[] bytes, int size) : MemoryStream(bytes)
{
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(size, buffer.Length)], cancellationToken);
}
sealed class HangingStream : Stream
{
    public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public bool Disposed { get; private set; }
    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) { Started.TrySetResult(); await Task.Delay(Timeout.Infinite, cancellationToken); return 0; }
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override void Flush() => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }
}
