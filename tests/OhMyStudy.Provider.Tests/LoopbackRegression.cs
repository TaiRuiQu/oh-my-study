using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

// Real sockets on an ephemeral loopback port. No HttpMessageHandler replacement, URL ACL,
// external service, real credential, or machine-wide listener registration is involved.
internal static class LoopbackRegression
{
    public static async Task RunAsync(JsonNode fixtures)
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            var native = fixtures[protocol.ToString().ToLowerInvariant()]!["final"]!;
            foreach (bool stream in new[] { false, true })
            {
                var reply = stream ? StreamBody(protocol, native) : native.ToJsonString();
                await using var server = new LoopbackServer(async (request, output, ct) =>
                {
                    Require(request.Method == "POST", "loopback POST");
                    string endpoint = protocol switch
                    {
                        Protocol.Responses => "/v1/responses",
                        Protocol.Messages => "/v1/messages",
                        _ => throw new InvalidOperationException()
                    };
                    Require(request.Target == endpoint, "loopback native endpoint");
                    string name = protocol == Protocol.Responses ? "Authorization" : "x-api-key";
                    Require(request.Headers[name] == (protocol == Protocol.Responses ? "Bearer " : "") + "synthetic-loopback-key", "loopback private header");
                    var body = JsonNode.Parse(request.Body)!;
                    Require(body[protocol switch { Protocol.Responses => "input", Protocol.Messages => "messages", _ => throw new InvalidOperationException() }] is JsonArray, "loopback JSON body");
                    if (protocol == Protocol.Responses) Require(body["store"]?.GetValue<bool>() == false && body["include"] is JsonArray, "loopback stateless Responses body");
                    if (protocol == Protocol.Messages) Require(body["cache_control"]?["type"]?.ToString() == "ephemeral", "loopback automatic Messages cache");
                    await ReplyAsync(output, 200, reply, stream, null, ct);
                });
                using var client = new ProviderClient(protocol, server.BaseUrl);
                var turn = await client.CallAsync(new("synthetic-loopback-key"), Options(stream), ProviderProtocol.Initial(protocol, "中文😀", []), []);
                Require(turn.Text == "完成。" && server.Count == 1, "actual response consumed");
                await server.AssertHealthyAsync();
            }

            await using var models = new LoopbackServer(async (request, output, ct) =>
            {
                Require(request.Method == "GET", "loopback model GET");
                bool second = request.Target.Contains('?', StringComparison.Ordinal);
                string cursorKey = protocol switch { Protocol.Responses => "after", Protocol.Messages => "after_id", _ => throw new InvalidOperationException() };
                Require(request.Target == "/v1/models" + (second ? "?" + cursorKey + "=next%20token" : ""), "actual encoded cursor");
                string data = protocol switch
                {
                    Protocol.Messages => second ? "{\"data\":[{\"id\":\"two\"}],\"has_more\":false}" : "{\"data\":[{\"id\":\"one\"}],\"has_more\":true,\"last_id\":\"next token\"}",
                    _ => second ? "{\"data\":[{\"id\":\"two\"}],\"has_more\":false}" : "{\"data\":[{\"id\":\"one\"}],\"has_more\":true,\"last_id\":\"next token\"}"
                };
                await ReplyAsync(output, 200, data, false, null, ct);
            });
            using var modelClient = new ProviderClient(protocol, models.BaseUrl);
            var first = await modelClient.ListModelsAsync(new("synthetic-loopback-key"));
            Require(first.Models.Count == 1 && first.Next == "next token", "network model first page");
            var last = await modelClient.ListModelsAsync(new("synthetic-loopback-key"), first.Next);
            Require(last.Models.Count == 1 && last.Next is null && models.Count == 2, "network model final page");
            await models.AssertHealthyAsync();
        }

        await using var target = new LoopbackServer((_, output, ct) => ReplyAsync(output, 200, "{}", false, null, ct));
        foreach (var protocol in new[] { Protocol.Responses, Protocol.Messages })
        {
            await using var redirect = new LoopbackServer((_, output, ct) => ReplyAsync(output, 307, "{}", false, target.BaseUrl, ct));
            using var client = new ProviderClient(protocol, redirect.BaseUrl);
            await RejectAsync(() => client.CallAsync(new("synthetic-loopback-key"), Options(), [], []), null);
            Require(redirect.Count == 1 && target.Count == 0, "credential redirect never followed");
            await redirect.AssertHealthyAsync();
        }

        foreach (var protocol in new[] { Protocol.Responses, Protocol.Messages })
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var hanging = new LoopbackServer(async (_, output, ct) =>
            {
                await output.WriteAsync(Encoding.ASCII.GetBytes("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream\r\nTransfer-Encoding: chunked\r\nConnection: close\r\n\r\n"), ct);
                await output.FlushAsync(ct); entered.TrySetResult();
                await Task.Delay(Timeout.InfiniteTimeSpan, ct);
            });
            using var client = new ProviderClient(protocol, hanging.BaseUrl);
            using var cancelled = new CancellationTokenSource();
            var pending = client.CallAsync(new("synthetic-loopback-key"), Options(true), [], [], cancellationToken: cancelled.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            cancelled.Cancel();
            await RejectAsync(() => pending, ErrorKind.Cancelled);
            Require(hanging.Count == 1, "in-flight socket cancellation never replays");
        }
    }

    private static RequestOptions Options(bool stream = false) => new() { Model = Model.Manual("manual-model"), MaxOutputTokens = 4096, Stream = stream, AllowUnknownCapabilities = true, Extra = [] };
    private static void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); }
    private static async Task RejectAsync(Func<Task> operation, ErrorKind? kind)
    {
        try { await operation().WaitAsync(TimeSpan.FromSeconds(10)); }
        catch (ProviderException error) { Require(kind is null || error.Kind == kind, "network error classification"); return; }
        throw new InvalidOperationException("expected loopback rejection");
    }
    private static string StreamBody(Protocol protocol, JsonNode native)
    {
        string Sse(JsonNode value) => "data: " + value.ToJsonString() + "\r\n\r\n";
        return protocol switch
        {
            Protocol.Responses => Sse(new JsonObject { ["type"] = "response.completed", ["response"] = native.DeepClone() }),
            Protocol.Messages => Sse(new JsonObject { ["type"] = "message_start", ["message"] = new JsonObject { ["id"] = "loopback", ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(), ["usage"] = new JsonObject { ["input_tokens"] = 1, ["output_tokens"] = 0 } } })
                + Sse(new JsonObject { ["type"] = "content_block_start", ["index"] = 0, ["content_block"] = new JsonObject { ["type"] = "text", ["text"] = "" } })
                + Sse(new JsonObject { ["type"] = "content_block_delta", ["index"] = 0, ["delta"] = new JsonObject { ["type"] = "text_delta", ["text"] = "完成。" } })
                + Sse(new JsonObject { ["type"] = "content_block_stop", ["index"] = 0 })
                + Sse(new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "end_turn" }, ["usage"] = new JsonObject { ["output_tokens"] = 1 } })
                + Sse(new JsonObject { ["type"] = "message_stop" }),
            _ => throw new InvalidOperationException()
        };
    }
    private static async Task ReplyAsync(NetworkStream output, int status, string text, bool chunked, string? location, CancellationToken ct)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(text);
        string headers = $"HTTP/1.1 {status} Synthetic\r\nConnection: close\r\nContent-Type: {(chunked ? "text/event-stream" : "application/json")}\r\n"
            + (location is null ? "" : "Location: " + location + "\r\n")
            + (chunked ? "Transfer-Encoding: chunked\r\n\r\n" : $"Content-Length: {bytes.Length}\r\n\r\n");
        await output.WriteAsync(Encoding.ASCII.GetBytes(headers), ct);
        if (chunked)
        {
            // One byte per HTTP chunk deliberately crosses every UTF-8 and SSE boundary.
            foreach (byte value in bytes) await output.WriteAsync(new byte[] { (byte)'1', 13, 10, value, 13, 10 }, ct);
            await output.WriteAsync("0\r\n\r\n"u8.ToArray(), ct);
        }
        else await output.WriteAsync(bytes, ct);
        await output.FlushAsync(ct);
    }

    private sealed record Request(string Method, string Target, Dictionary<string, string> Headers, string Body);
    private sealed class LoopbackServer : IAsyncDisposable
    {
        private readonly TcpListener listener = new(IPAddress.Loopback, 0);
        private readonly CancellationTokenSource lifetime = new(TimeSpan.FromSeconds(45));
        private readonly Task worker;
        private Exception? failure;
        private int count;
        public int Count => Volatile.Read(ref count);
        public string BaseUrl { get; }
        public LoopbackServer(Func<Request, NetworkStream, CancellationToken, Task> respond)
        {
            listener.Start();
            BaseUrl = $"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/v1/";
            worker = ServeAsync(respond);
        }
        private async Task ServeAsync(Func<Request, NetworkStream, CancellationToken, Task> respond)
        {
            try
            {
                while (!lifetime.IsCancellationRequested)
                {
                    using var socket = await listener.AcceptTcpClientAsync(lifetime.Token);
                    await using var input = socket.GetStream();
                    var request = await ReadAsync(input, lifetime.Token);
                    Interlocked.Increment(ref count);
                    await respond(request, input, lifetime.Token);
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
            catch (ObjectDisposedException) when (lifetime.IsCancellationRequested) { }
            catch (SocketException) when (lifetime.IsCancellationRequested) { }
            catch (Exception error) { failure = error; }
        }
        public Task AssertHealthyAsync()
        {
            if (failure is not null) throw new InvalidOperationException("Loopback fixture failed", failure);
            return Task.CompletedTask;
        }
        private static async Task<Request> ReadAsync(NetworkStream input, CancellationToken ct)
        {
            var header = new List<byte>(); var one = new byte[1];
            while (true)
            {
                if (await input.ReadAsync(one, ct) == 0) throw new EndOfStreamException();
                header.Add(one[0]);
                int n = header.Count;
                if (n >= 4 && header[n - 4] == 13 && header[n - 3] == 10 && header[n - 2] == 13 && header[n - 1] == 10) break;
                if (n > 16384) throw new InvalidDataException("Fixture request headers exceed bound");
            }
            string[] lines = Encoding.ASCII.GetString(header.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
            string[] first = lines[0].Split(' ');
            var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string line in lines.Skip(1)) { int colon = line.IndexOf(':'); headers.Add(line[..colon], line[(colon + 1)..].Trim()); }
            int length = headers.TryGetValue("Content-Length", out var value) ? int.Parse(value, System.Globalization.CultureInfo.InvariantCulture) : 0;
            if (length is < 0 or > 1024 * 1024) throw new InvalidDataException("Fixture body exceeds bound");
            var body = new byte[length]; await input.ReadExactlyAsync(body, ct);
            return new(first[0], first[1], headers, Encoding.UTF8.GetString(body));
        }
        public async ValueTask DisposeAsync()
        {
            lifetime.Cancel(); listener.Stop(); await worker; lifetime.Dispose();
            await AssertHealthyAsync();
        }
    }
}
