using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

internal static class FilesApiRegression
{
    public static async Task RunAsync(JsonNode fixtures)
    {
        foreach (var protocol in new[] { Protocol.Responses, Protocol.Messages })
        {
            await RoundTrip(protocol, fixtures);
            await PartialFailure(protocol);
        }
        await Disabled(fixtures);
        await UserDataPurpose(fixtures);
        await Cancellation();
    }

    private static RequestOptions Options() => new() { Model = Model.Manual("synthetic"), MaxOutputTokens = 1024, Stream = false, AllowUnknownCapabilities = true, Extra = [] };
    private static Image Image(string text) => new() { Mime = "image/png", Base64 = Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) };
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static HttpResponseMessage Response(string body, int status = 200) => new((HttpStatusCode)status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };

    private static async Task RoundTrip(Protocol protocol, JsonNode fixtures)
    {
        int uploads = 0, calls = 0;
        bool rejectDelete = true;
        var deleted = new List<string>();
        var replies = fixtures[protocol == Protocol.Responses ? "responses" : "messages"]!;
        using var http = new HttpClient(new FakeHandler(async (request, token) =>
        {
            Require(request.RequestUri!.AbsolutePath.StartsWith("/gateway/custom/", StringComparison.Ordinal), "Files API preserves endpoint prefix");
            Require(request.Headers.GetValues("x-study").Single() == "synthetic-header", "Files API preserves custom headers");
            if (request.Method == HttpMethod.Delete)
            {
                string id = request.RequestUri.Segments[^1]; deleted.Add(id);
                if (rejectDelete && id == "file-1") return Response("{}", 500);
                return Response("{}", 404);
            }
            if (request.RequestUri.AbsolutePath.EndsWith("/files", StringComparison.Ordinal))
            {
                Require(request.Method == HttpMethod.Post && request.Content is MultipartFormDataContent, "Files API uploads multipart file content");
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                string multipart = await request.Content!.ReadAsStringAsync(token);
                foreach (string name in parts.Keys)
                    Require(multipart.Contains($"Content-Disposition: form-data; name=\"{name}\"", StringComparison.Ordinal), "Files API quotes every multipart field name for strict form parsers");
                string filename = parts["file"].Headers.ContentDisposition!.FileName!.Trim('"');
                Require(multipart.Contains($"name=\"file\"; filename=\"{filename}\"", StringComparison.Ordinal), "Files API quotes the multipart filename for strict form parsers");
                Require(parts["file"].Headers.ContentType!.MediaType == "image/png" && (await parts["file"].ReadAsByteArrayAsync(token)).Length > 0, "Files API sends binary image without base64");
                if (protocol == Protocol.Responses)
                {
                    Require(request.Headers.Authorization?.Parameter == "synthetic-key", "Responses file authentication");
                    Require(await parts["purpose"].ReadAsStringAsync(token) == "user_data" && await parts["expires_after[anchor]"].ReadAsStringAsync(token) == "created_at"
                        && await parts["expires_after[seconds]"].ReadAsStringAsync(token) == "3600", "Responses files expire after one hour");
                }
                else
                {
                    Require(request.Headers.GetValues("x-api-key").Single() == "synthetic-key" && request.Headers.Contains("anthropic-version"), "Messages file authentication");
                    Require(await parts["expires_in_seconds"].ReadAsStringAsync(token) == "3600" && !parts.ContainsKey("purpose")
                        && !request.Headers.Contains("anthropic-beta"), "Messages files use current one-hour expiration contract");
                }
                uploads++;
                return Response(new JsonObject { ["id"] = "file-" + uploads }.ToJsonString());
            }
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(token))!;
            string serialized = body.ToJsonString();
            Require(!serialized.Contains("base64", StringComparison.Ordinal) && serialized.Contains("file-1", StringComparison.Ordinal), "model requests reference uploaded file IDs");
            if (calls > 0) Require(serialized.Contains("file-2", StringComparison.Ordinal), "new crops receive their own uploaded file");
            calls++;
            return Response((calls == 1 ? replies["tool"]! : replies["final"]!).ToJsonString());
        }));
        using var client = new ProviderClient(http, protocol, "https://example.invalid/gateway/custom", new Dictionary<string, string> { ["x-study"] = "synthetic-header" }, supportsFilesApi: true);
        var credentials = new ProviderCredentials("synthetic-key");
        var history = ProviderProtocol.Initial(protocol, "Synthetic exam", [Image("whole-page"), Image("whole-page")]);
        string original = history.ToJsonString();
        var first = await client.CallAsync(credentials, Options(), history, []);
        Require(uploads == 1 && history.ToJsonString() == original, "duplicate images upload once without mutating caller history");
        ProviderProtocol.ContinueWith(protocol, history, first, first.Calls.Select(call => new ToolResult { Call = call, Text = "{}", Images = [Image("whole-page"), Image("crop")], IsError = false }).ToList());
        await client.CallAsync(credentials, Options(), history, []);
        await client.CallAsync(credentials, Options(), history, []);
        Require(uploads == 2 && calls == 3, "uploads are reused across model rounds and repeated crops");
        try { await client.DeleteFilesAsync(credentials); throw new InvalidOperationException("cleanup failure was ignored"); }
        catch (AggregateException) { }
        Require(deleted.SequenceEqual(["file-1", "file-2"]), "cleanup attempts every uploaded file even after a failure");
        rejectDelete = false;
        await client.DeleteFilesAsync(credentials);
        Require(deleted.SequenceEqual(["file-1", "file-2", "file-1"]), "cleanup retries only remaining files and accepts expiration 404");
    }

    private static async Task PartialFailure(Protocol protocol)
    {
        int uploads = 0, deleted = 0, modelCalls = 0;
        using var http = new HttpClient(new FakeHandler((request, _) =>
        {
            if (request.Method == HttpMethod.Delete) { deleted++; return Task.FromResult(Response("{}")); }
            if (!request.RequestUri!.AbsolutePath.EndsWith("/files", StringComparison.Ordinal)) modelCalls++;
            return Task.FromResult(++uploads == 1 ? Response("""{"id":"file-partial"}""") : Response("""{"error":{"message":"unsupported expiration parameter"}}""", 400));
        }));
        using var client = new ProviderClient(http, protocol, "https://example.invalid/v1", supportsFilesApi: true);
        var credentials = new ProviderCredentials("synthetic-key");
        try
        {
            await client.CallAsync(credentials, Options(), ProviderProtocol.Initial(protocol, "exam", [Image("first"), Image("second")]), []);
            throw new InvalidOperationException("file rejection was ignored");
        }
        catch (ProviderException error) { Require(error.Diagnostic == "files_api_rejected", "file rejection provides an actionable diagnostic"); }
        await client.DeleteFilesAsync(credentials);
        Require(uploads == 2 && deleted == 1 && modelCalls == 0, "partial upload failure cleans prior files without fallback to large inline requests");
    }

    private static async Task Disabled(JsonNode fixtures)
    {
        int calls = 0;
        using var http = new HttpClient(new FakeHandler(async (request, token) =>
        {
            Require(request.RequestUri!.AbsolutePath == "/v1/responses", "disabled Files API does not upload");
            Require((await request.Content!.ReadAsStringAsync(token)).Contains("base64", StringComparison.Ordinal), "disabled Files API preserves inline images");
            calls++; return Response(fixtures["responses"]!["final"]!.ToJsonString());
        }));
        using var client = new ProviderClient(http, Protocol.Responses, "https://example.invalid/v1");
        await client.CallAsync(new("synthetic-key"), Options(), ProviderProtocol.Initial(Protocol.Responses, "exam", [Image("page")]), []);
        await client.DeleteFilesAsync(new("synthetic-key"));
        Require(calls == 1, "Files API is opt-in");
    }

    private static async Task UserDataPurpose(JsonNode fixtures)
    {
        int uploads = 0, calls = 0, deleted = 0;
        using var http = new HttpClient(new FakeHandler(async (request, token) =>
        {
            if (request.Method == HttpMethod.Delete)
            {
                Require(request.RequestUri!.AbsolutePath == "/files/file-api-study", "user-data cleanup uses the uploaded file ID");
                deleted++;
                return Response("{}");
            }
            if (request.RequestUri!.AbsolutePath == "/files")
            {
                var parts = ((MultipartFormDataContent)request.Content!).ToDictionary(p => p.Headers.ContentDisposition!.Name!.Trim('"'));
                if (await parts["purpose"].ReadAsStringAsync(token) != "user_data")
                    return Response("""{"error":{"message":"The 'vision' purpose is not supported. Supported purposes: [\"user_data\"]","type":"invalid_request_error"}}""", 400);
                Require(await parts["expires_after[anchor]"].ReadAsStringAsync(token) == "created_at"
                    && await parts["expires_after[seconds]"].ReadAsStringAsync(token) == "3600", "user-data uploads retain one-hour expiration");
                uploads++;
                return Response("""{"id":"file-api-study","purpose":"user_data"}""");
            }
            Require(request.RequestUri.AbsolutePath == "/responses", "user-data files reach the Responses endpoint");
            string body = await request.Content!.ReadAsStringAsync(token);
            Require(body.Contains("file-api-study", StringComparison.Ordinal) && !body.Contains("base64", StringComparison.Ordinal), "inference reuses the user-data file without inline images");
            calls++;
            return Response(fixtures["responses"]!["final"]!.ToJsonString());
        }));
        using var client = new ProviderClient(http, Protocol.Responses, "https://api.deepseek.com", supportsFilesApi: true);
        var credentials = new ProviderCredentials("synthetic-key");
        var history = ProviderProtocol.Initial(Protocol.Responses, "exam", [Image("page")]);
        await client.CallAsync(credentials, Options(), history, []);
        await client.CallAsync(credentials, Options(), history, []);
        await client.DeleteFilesAsync(credentials);
        Require(uploads == 1 && calls == 2 && deleted == 1, "user-data-only providers support upload, reuse, inference and cleanup");
    }

    private static async Task Cancellation()
    {
        using var cancellation = new CancellationTokenSource();
        using var http = new HttpClient(new FakeHandler(async (_, token) =>
        {
            cancellation.Cancel(); await Task.Delay(Timeout.Infinite, token); return Response("{}");
        }));
        using var client = new ProviderClient(http, Protocol.Responses, "https://example.invalid/v1", supportsFilesApi: true);
        try
        {
            await client.CallAsync(new("synthetic-key"), Options(), ProviderProtocol.Initial(Protocol.Responses, "exam", [Image("page")]), [], cancellationToken: cancellation.Token);
            throw new InvalidOperationException("upload ignored cancellation");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
    }
}
