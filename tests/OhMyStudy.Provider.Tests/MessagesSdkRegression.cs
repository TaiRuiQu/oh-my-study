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
        await GatewayDiagnostics();
        await ConnectionProbeDetails();
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

    private static async Task GatewayDiagnostics()
    {
        foreach (var (status, type, message, reason, kind) in new[]
        {
            (400, "invalid_request_error", "Unrecognized key cache_control", "cache_control_rejected", ErrorKind.InvalidRequest),
            (400, "invalid_request_error", "Wrong endpoint for this model", "model_endpoint_mismatch", ErrorKind.InvalidRequest),
            (400, "unsupported_model", "Model is unavailable", "model_not_available", ErrorKind.UnsupportedParameter),
            (400, "invalid_request_error", "output_config.effort is not supported", "thinking_effort_rejected", ErrorKind.UnsupportedParameter),
            (400, "invalid_request_error", "thinking.budget_tokens is invalid", "thinking_budget_rejected", ErrorKind.InvalidRequest),
            (400, "invalid_request_error", "tools.0.input_schema is invalid", "tool_schema_rejected", ErrorKind.InvalidRequest),
            (400, "invalid_request_error", "max_tokens is too large", "output_token_limit_rejected", ErrorKind.InvalidRequest),
            (401, "authentication_error", "Invalid credentials", "credential_rejected", ErrorKind.Authentication),
            (402, "invalid_request_error", "Payment required", "billing_or_credit_limit", ErrorKind.Quota),
            (403, "upgrade_required", "This plan does not include API access", "api_plan_required", ErrorKind.Authentication),
            (404, "not_found_error", "Not found", "model_or_endpoint_not_found", ErrorKind.InvalidRequest)
        })
        {
            int requests = 0;
            var payload = new JsonObject
            {
                ["type"] = "error",
                ["error"] = new JsonObject { ["type"] = type, ["message"] = message + " SYNTHETIC_SECRET_ECHO student text" }
            };
            using var http = new HttpClient(new FakeHandler(async (request, ct) =>
            {
                requests++;
                Require(request.RequestUri!.AbsolutePath == "/provider/v1/messages", "configured Command Code prefix is appended exactly once");
                Require(request.Headers.GetValues("x-api-key").Single() == "synthetic-key", "Messages SDK authentication remains x-api-key");
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                Require(body["model"]!.ToString() == "claude-haiku-5-5"
                    && body["thinking"]!["type"]!.ToString() == "adaptive"
                    && body["output_config"]!["effort"]!.ToString() == "medium", "configured model and medium effort retain their native request fields");
                var response = Reply(payload.ToJsonString(), false, status);
                response.Headers.TryAddWithoutValidation("request-id", "req_synthetic_093f81bd");
                return response;
            }));
            // This handler intercepts every request; the production-looking URL is never contacted.
            using var client = new ProviderClient(http, Protocol.Messages, "https://api.commandcode.ai/provider/v1");
            var error = await Reject(() => client.CallAsync(new("synthetic-key"), Options() with
            {
                Model = Model.Manual("claude-haiku-5-5"),
                Thinking = new AdaptiveThinking { Effort = "medium" }
            }, ProviderProtocol.Initial(Protocol.Messages, "Synthetic connection check", []), []), kind);
            string logged = error.ToString();
            Require(requests == 1 && error.Diagnostic == reason && error.RequestId == "req_synthetic_093f81bd", "rejection keeps only safe diagnostic metadata without replaying");
            Require(logged.Contains($"HTTP={status}", StringComparison.Ordinal) && logged.Contains(reason, StringComparison.Ordinal)
                && logged.Contains("req_synthetic_093f81bd", StringComparison.Ordinal), "exception log includes HTTP status, reason and request ID");
            Require(!logged.Contains("SYNTHETIC_SECRET", StringComparison.Ordinal) && !logged.Contains("student text", StringComparison.Ordinal)
                && !error.Message.Contains("req_synthetic", StringComparison.Ordinal), "raw provider messages and diagnostic metadata stay out of UI messages");
        }
        var invalidId = ProviderErrors.Classify(400, Json("""{"request_id":"SYNTHETIC_SECRET_ECHO","error":{"message":"SYNTHETIC_SECRET_ECHO"}}"""), requestId: "https://example.invalid/private");
        Require(invalidId.RequestId is null && !invalidId.ToString().Contains("SYNTHETIC_SECRET", StringComparison.Ordinal), "untrusted request ID formats are dropped");
        var bodyId = ProviderErrors.Classify(400, Json("""{"request_id":"req_synthetic_body_093f81bd","error":{}}"""));
        Require(bodyId.RequestId == "req_synthetic_body_093f81bd", "safe body request ID is retained when the response header is absent");
    }

    private static async Task ConnectionProbeDetails()
    {
        const string key = "synthetic:key+/connection-secret";
        string encoded = Uri.EscapeDataString(key), base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(key));
        var message = "Model access denied for this account. " + key + " " + encoded + " " + base64 + "\nno injected log line";
        int calls = 0;
        using var http = new HttpClient(new FakeHandler(async (request, ct) =>
        {
            calls++;
            if (calls == 1)
            {
                var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(ct))!;
                Require(body["messages"]!.AsArray().Count == 1
                    && body["messages"]![0]!["content"]!.AsArray().Count == 1
                    && body["messages"]![0]!["content"]![0]!["text"]!.ToString() == "Reply with OK."
                    && body["max_tokens"]!.GetValue<int>() == 256 && body["stream"]!.GetValue<bool>() == false
                    && body["tools"]!.AsArray().Count == 0 && body["thinking"] is null && body["system"] is null,
                    "detailed connection probe accepts no documents, history, tools or extra instructions");
            }
            return Reply(new JsonObject { ["error"] = new JsonObject { ["type"] = "permission_error", ["message"] = message } }.ToJsonString(), false, 403);
        }));
        using var client = new ProviderClient(http, Protocol.Messages, "https://example.invalid/provider/v1");
        var probe = await Reject(() => client.TestConnectionAsync(new(key), "claude-haiku-5-5"), ErrorKind.Authentication);
        Require(calls == 1 && probe.ConnectionDetail!.Contains("Model access denied for this account.", StringComparison.Ordinal), "connection probe preserves the actual rejection reason with one request");
        string logged = probe.ToString();
        string detail = probe.ConnectionDetail!;
        Require(!logged.Contains(key, StringComparison.Ordinal) && !logged.Contains(encoded, StringComparison.Ordinal)
            && !logged.Contains(base64, StringComparison.Ordinal) && !detail.Contains('\n')
            && !probe.Message.Contains("Model access denied", StringComparison.Ordinal), "connection details redact secret representations and normalize controls without changing UI feedback");
        var ordinary = await Reject(() => client.CallAsync(new(key), Options(), ProviderProtocol.Initial(Protocol.Messages, "Synthetic grading context", []), []), ErrorKind.Authentication);
        Require(calls == 2 && ordinary.ConnectionDetail is null && !ordinary.ToString().Contains("Model access denied", StringComparison.Ordinal), "ordinary calls on the same client cannot retain probe-style error text");

        using var htmlHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)
        { Content = new StringContent("<html>SECRET_SENSITIVE_BODY</html>", Encoding.UTF8, "text/html") })));
        using var htmlClient = new ProviderClient(htmlHttp, Protocol.Messages, "https://example.invalid/v1");
        var html = await Reject(() => htmlClient.TestConnectionAsync(new(key), "synthetic-model"), ErrorKind.Authentication);
        Require(html.ConnectionDetail!.Contains("HTML", StringComparison.Ordinal) && !html.ToString().Contains("SECRET_SENSITIVE_BODY", StringComparison.Ordinal), "HTML access rejection is distinguished without retaining HTML");

        using var longHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Reply(new JsonObject
        { ["error"] = new JsonObject { ["message"] = new string('a', 2048) + key } }.ToJsonString(), false, 403))));
        using var longClient = new ProviderClient(longHttp, Protocol.Messages, "https://example.invalid/v1");
        var bounded = await Reject(() => longClient.TestConnectionAsync(new(key), "synthetic-model"), ErrorKind.Authentication);
        Require(bounded.ConnectionDetail!.Length <= 1025 && !bounded.ToString().Contains(key, StringComparison.Ordinal), "connection rejection detail is bounded after redaction");

        foreach (string payload in new[]
        {
            new JsonObject { ["message"] = "Access restricted " + key }.ToJsonString(),
            new JsonObject { ["error"] = "Access restricted " + key }.ToJsonString(),
            "Access restricted " + key
        })
        {
            using var alternateHttp = new HttpClient(new FakeHandler((_, _) => Task.FromResult(Reply(payload, false, 403))));
            using var alternateClient = new ProviderClient(alternateHttp, Protocol.Messages, "https://example.invalid/v1");
            var alternate = await Reject(() => alternateClient.TestConnectionAsync(new(key), "synthetic-model"), ErrorKind.Authentication);
            Require(alternate.ConnectionDetail!.StartsWith("Access restricted", StringComparison.Ordinal)
                && !alternate.ToString().Contains(key, StringComparison.Ordinal), "connection probes handle gateway JSON and plain-text rejection shapes safely");
        }
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
