using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

// Synthetic payloads only: these tests cannot contact a supplier or incur model charges.
internal static class MessagesSdkRegression
{
    public static async Task RunAsync(JsonNode fixtures)
    {
        var credentials = new ProviderCredentials("synthetic-key");
        var toolReply = fixtures["messages"]!["tool"]!;
        var finalReply = fixtures["messages"]!["final"]!;
        var tool = new Tool { Name = "read_image", Description = "Read page", Schema = Json("""{"type":"object","properties":{"page":{"type":"integer"}},"required":["page"]}""") };
        foreach (var streaming in new[] { false, true })
        {
            var history = ProviderProtocol.Initial(Protocol.Messages, "整卷😀", [new Image { Mime = "image/png", Base64 = "SYNTHETIC_IMAGE" }]);
            var calls = 0;
            JsonNode? firstAssistant = null;
            using var http = new HttpClient(new FakeHandler(async (request, ct) =>
            {
                Require(request.RequestUri!.AbsolutePath == "/gateway/custom/messages", "custom endpoint prefix preserved");
                Require(request.Headers.GetValues("x-study").Single() == "synthetic-header", "custom header preserved");
                Require(request.Headers.GetValues("anthropic-version").Single() == "2023-06-01", "SDK version header");
                Require(request.Headers.GetValues("x-api-key").Single() == "synthetic-key" && request.Headers.Authorization is null, "private SDK credentials only");
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                Require(body["cache_control"]?["type"]?.ToString() == "ephemeral" && body["cache_control"]?["ttl"] is null, "automatic cache uses default TTL");
                Require(body["stream"]!.GetValue<bool>() == (calls == 0 && streaming) && body["system"]?.ToString() == "规则", "native extras and stream field");
                Require(body["tools"]?[0]?["input_schema"]?["required"]?[0]?.ToString() == "page", "SDK tool schema");
                Require(body["thinking"]?["type"]?.ToString() == "adaptive" && body["output_config"]?["effort"]?.ToString() == "high", "SDK thinking fields");
                if (++calls == 1)
                {
                    Require(body["messages"]!.AsArray().Count == 1, "first turn history");
                    var sse = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "messages.synthetic.sse"), ct);
                    return Reply(streaming ? sse : toolReply.ToJsonString(), streaming);
                }
                Require(JsonNode.DeepEquals(body["messages"]![1], firstAssistant), "signed and redacted context preserved");
                Require(body["messages"]![2]?["content"]?[0]?["content"]?[1]?["type"]?.ToString() == "image", "image tool result preserved");
                Require(!body["messages"]!.ToJsonString().Contains("cache_control", StringComparison.Ordinal), "automatic cache does not add block markers");
                return Reply(finalReply.ToJsonString(), false);
            }));
            using var client = new ProviderClient(http, Protocol.Messages, "https://example.invalid/gateway/custom", new Dictionary<string, string> { ["x-study"] = "synthetic-header" });
            var options = Options(streaming) with { Thinking = new AdaptiveThinking { Effort = "high" }, Extra = new() { ["system"] = Json("\"规则\"") } };
            var progress = new List<ProviderProgress>();
            var turn = await client.CallAsync(credentials, options, history, [tool], progress.Add);
            Require(turn.Calls.Count == 1 && turn.Calls[0].Arguments.GetProperty("page").GetInt32() == 1, "SDK fragmented tool JSON");
            Require(turn.Usage.Input == 100 && turn.Usage.CacheRead == 15 && turn.Usage.CacheWrite == 5 && turn.Usage.Output == 30, "cache usage survives SDK response mapping");
            if (streaming) Require(progress.Any(p => p.ToolName == "read_image"), "SDK tool progress");
            firstAssistant = JsonNode.Parse(turn.Context.GetRawText());
            ProviderProtocol.ContinueWith(Protocol.Messages, history, turn, [new ToolResult { Call = turn.Calls[0], Text = "page 1", IsError = false, Images = [new Image { Mime = "image/png", Base64 = "SYNTHETIC_TOOL_IMAGE" }] }]);
            var snapshot = history.ToJsonString();
            var result = await client.CallAsync(credentials, options with { Stream = false }, history, [tool]);
            Require(result.Text == "完成。" && snapshot == history.ToJsonString() && calls == 2, "cached continuation without history mutation or hidden retries");
        }

        foreach (var key in new[] { "cache_control", "messages", "stream", "tools", "thinking", "output_config" })
            await Reject(() => Task.FromResult(ProviderProtocol.BuildRequest(Protocol.Messages, Options() with { Extra = new() { [key] = Json("null") } }, [], [])), ErrorKind.InvalidRequest);
        await Reject(() => Task.FromResult(ProviderProtocol.BuildRequest(Protocol.Messages, Options() with { MaxOutputTokens = ulong.MaxValue }, [], [])), ErrorKind.InvalidRequest);

        foreach (var (status, payload, kind) in new[]
        {
            (401, "SYNTHETIC_SECRET_ECHO", ErrorKind.Authentication),
            (403, "not JSON", ErrorKind.Authentication),
            (429, "{}", ErrorKind.RateLimit),
            (400, "{\"error\":{\"type\":\"billing_error\",\"message\":\"SYNTHETIC_SECRET_ECHO\"}}", ErrorKind.Quota),
            (400, "{\"error\":{\"message\":\"unsupported field SYNTHETIC_SECRET_ECHO\"}}", ErrorKind.UnsupportedParameter),
            (503, "not JSON", ErrorKind.Server)
        })
        {
            var calls = 0;
            using var http = new HttpClient(new FakeHandler((_, _) => { calls++; var reply = Reply(payload, false, status); reply.Headers.TryAddWithoutValidation("Retry-After", "7"); return Task.FromResult(reply); }));
            using var client = new ProviderClient(http, Protocol.Messages, "https://example.invalid/v1");
            var error = await Reject(() => client.CallAsync(credentials, Options(), [], []), kind);
            Require(error.Error.Status == status && error.Error.RetryAfter == "7" && calls == 1 && !error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal), "sanitized rejection and no implicit retries");
            await Reject(() => client.ListModelsAsync(credentials), kind);
        }

        var attempts = 0;
        using (var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(++attempts < 3 ? Reply("{}", false, 429) : Reply(finalReply.ToJsonString(), false)))))
        using (var client = new ProviderClient(http, Protocol.Messages, "https://example.invalid/v1"))
        {
            await client.CallWithRetryAsync(credentials, Options(), [], [], Policy());
            Require(attempts == 3, "only explicit bounded HTTP retries");
        }
        foreach (var (native, kind) in new[] { ("overloaded_error", ErrorKind.Server), ("rate_limit_error", ErrorKind.RateLimit), ("billing_error", ErrorKind.Quota), ("authentication_error", ErrorKind.Authentication) })
        {
            attempts = 0;
            using var http = new HttpClient(new FakeHandler((_, _) => { attempts++; return Task.FromResult(Reply("event: error\ndata: {\"type\":\"error\",\"error\":{\"type\":\"" + native + "\",\"message\":\"SYNTHETIC_SECRET_ECHO\"}}\n\n", true)); }));
            using var client = new ProviderClient(http, Protocol.Messages, "https://example.invalid/v1");
            var error = await Reject(() => client.CallWithRetryAsync(credentials, Options(true), [], [], Policy()), kind);
            Require(attempts == 1 && error.Error.Status is null && !error.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal), "native stream failure is sanitized and never replayed");
        }
        attempts = 0;
        using (var http = new HttpClient(new FakeHandler((_, _) => { attempts++; throw new HttpRequestException("SYNTHETIC_SECRET_ECHO"); })))
        using (var client = new ProviderClient(http, Protocol.Messages, "https://example.invalid/v1"))
        {
            await Reject(() => client.CallWithRetryAsync(credentials, Options(), [], [], Policy()), ErrorKind.Network);
            Require(attempts == 1, "ambiguous network failure never replays");
        }

        foreach (var streaming in new[] { false, true })
        {
            using var cancellation = new CancellationTokenSource();
            var hanging = new HangingStream();
            using var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(hanging) })));
            using var client = new ProviderClient(http, Protocol.Messages, "https://example.invalid/v1");
            var pending = client.CallAsync(credentials, Options(streaming), [], [], cancellationToken: cancellation.Token);
            await hanging.Started.Task.WaitAsync(TimeSpan.FromSeconds(5));
            cancellation.Cancel();
            await Reject(() => pending, ErrorKind.Cancelled);
            Require(hanging.Disposed, "cancelled SDK body disposed");
        }
        using (var http = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Reply("event: message_start\ndata: {\"type\":\"message_start\",\"message\":{\"content\":[]}}\n\n", true)))))
        using (var client = new ProviderClient(http, Protocol.Messages, "https://example.invalid/v1"))
            await Reject(() => client.CallAsync(credentials, Options(true), [], []), ErrorKind.Incomplete);
    }

    private static RequestOptions Options(bool stream = false) => new() { Model = Model.Manual("manual-model"), MaxOutputTokens = 4096, Stream = stream, AllowUnknownCapabilities = true, Extra = [] };
    private static ProviderRetryPolicy Policy() => new() { MaximumAttempts = 3, InitialDelay = TimeSpan.Zero, MaximumDelay = TimeSpan.Zero };
    private static JsonElement Json(string value) => JsonElement.Parse(value);
    private static void Require(bool condition, string label) { if (!condition) throw new InvalidOperationException(label); }
    private static async Task<ProviderException> Reject(Func<Task> action, ErrorKind kind)
    {
        try { await action().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (ProviderException error) { Require(error.Kind == kind, $"expected {kind}, got {error.Kind}"); return error; }
        throw new InvalidOperationException("expected SDK rejection");
    }
    private static HttpResponseMessage Reply(string payload, bool stream, int status = 200)
        => new((HttpStatusCode)status) { Content = new StringContent(payload, Encoding.UTF8, stream ? "text/event-stream" : "application/json") };
}
