using System.Net;
using System.Text;
using System.Text.Json;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Infrastructure.Diagnostics;

internal static partial class Program
{
    private static async Task DiagnosticChecks()
    {
        var entries = new List<(string Phase, string Detail, bool Failed)>();
        void Record(string phase, string detail, bool failed) => entries.Add((phase, detail, failed));
        OperationDiagnostics.Written += Record;
        try
        {
            using var scope = OperationDiagnostics.Begin("synthetic-search-trace");
            using var inner = new DiagnosticHandler();
            using var http = new HttpClient(new DiagnosticHttpHandler(inner));
            using var request = new HttpRequestMessage(HttpMethod.Post, "https://synthetic.invalid/v1/embeddings?api_key=query-secret")
            {
                Content = new StringContent("""{"model":"synthetic-model","input":["synthetic question"],"api_key":"body-secret"}""", Encoding.UTF8, "application/json")
            };
            request.Headers.Authorization = new("Bearer", "header-secret");
            using var response = await http.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.BadRequest && (await response.Content.ReadAsStringAsync()).Contains("encoding_format", StringComparison.Ordinal), "HTTP diagnostics preserve provider rejection and response body");
            var rejected = entries.Single(entry => entry.Phase == "Http.Rejected");
            using var failure = JsonDocument.Parse(rejected.Detail);
            Check(failure.RootElement.GetProperty("request").GetProperty("method").GetString() == "POST" && rejected.Detail.Contains("synthetic question", StringComparison.Ordinal)
                && rejected.Detail.Contains("encoding_format", StringComparison.Ordinal) && rejected.Failed, "logs preserve request payload and actionable provider error details");
            Check(entries.Count == 1 && failure.RootElement.GetProperty("request_id").GetString() is { Length: > 0 }
                && failure.RootElement.GetProperty("correlation_id").GetString() == "synthetic-search-trace", "HTTP rejection combines request and response in one correlated failure");
            string logs = string.Join('\n', entries.Select(entry => entry.Detail));
            Check(new[] { "header-secret", "body-secret", "query-secret", "cookie-secret", "provider-token-secret" }.All(secret => !logs.Contains(secret, StringComparison.Ordinal)), "headers, echoed credentials, nested secret fields and URL tokens are redacted");
            Check(entries.All(entry => { using var document = JsonDocument.Parse(entry.Detail); return document.RootElement.TryGetProperty("sequence", out _); }), "diagnostic events retain a reconstructable sequence");

            entries.Clear();
            inner.Success = true;
            using (var successful = await http.PostAsync("https://synthetic.invalid/v1/embeddings", new StringContent("synthetic successful request")))
                Check((await successful.Content.ReadAsStringAsync()).Contains("synthetic successful response", StringComparison.Ordinal), "successful JSON bodies remain available to the caller");
            Check(entries.Count == 1 && entries[0].Phase == "Http.Response" && !entries[0].Failed
                && !entries[0].Detail.Contains("synthetic successful", StringComparison.Ordinal)
                && !entries[0].Detail.Contains("headers", StringComparison.Ordinal), "successful HTTP calls emit one summary without headers or payloads");

            entries.Clear(); inner.Success = false;
            using (var multipart = new MultipartFormDataContent())
            {
                multipart.Add(new ByteArrayContent(Encoding.UTF8.GetBytes("synthetic-private-image-bytes")), "file", "synthetic.png");
                using var uploadResponse = await http.PostAsync("https://synthetic.invalid/v1/files", multipart);
                var uploadFailure = entries.Single(entry => entry.Phase == "Http.Rejected");
                using var uploadJson = JsonDocument.Parse(uploadFailure.Detail);
                Check(!uploadFailure.Detail.Contains("synthetic-private-image-bytes", StringComparison.Ordinal)
                    && uploadJson.RootElement.GetProperty("request").GetProperty("body_bytes").GetInt64() > 0
                    && uploadFailure.Detail.Contains("multipart file content omitted", StringComparison.Ordinal), "multipart diagnostics retain byte counts without logging image bytes");
            }

            entries.Clear();
            inner.StreamFailure = true;
            using var streamRequest = new HttpRequestMessage(HttpMethod.Post, "https://synthetic.invalid/v1/responses");
            streamRequest.Headers.Authorization = new("Bearer", "header-secret");
            using var streamed = await http.SendAsync(streamRequest, HttpCompletionOption.ResponseHeadersRead);
            await using var body = await streamed.Content.ReadAsStreamAsync();
            var buffer = new byte[2];
            Check(await body.ReadAsync(buffer) == 2, "diagnostic stream preserves response bytes");
            await Fails(async () => _ = await body.ReadAsync(buffer), "diagnostic stream preserves the original transport exception");
            var interrupted = entries.Single(entry => entry.Phase == "Http.BodyFailed");
            using var interruption = JsonDocument.Parse(interrupted.Detail);
            Check(interrupted.Detail.Contains("Synthetic interrupted response", StringComparison.Ordinal) && !interrupted.Detail.Contains("header-secret", StringComparison.Ordinal)
                && interruption.RootElement.GetProperty("bytes_received").GetInt64() == 2
                && interruption.RootElement.GetProperty("request").GetProperty("url").GetString() == "https://synthetic.invalid/v1/responses", "stream failure logs retain request, exception chain and interruption position without secrets");

            void Broken(string phase, string detail, bool failed) => throw new IOException("Synthetic unavailable log sink");
            OperationDiagnostics.Written += Broken;
            try { OperationDiagnostics.Record("Synthetic.LogSink", new() { ["step"] = "continue" }); }
            finally { OperationDiagnostics.Written -= Broken; }
            Check(entries.Any(entry => entry.Phase == "Synthetic.LogSink"), "a failing diagnostic sink does not interrupt the operation or other sinks");
            string redacted = DiagnosticRedaction.Body("""{"url":"https://user:userinfo-secret@example.invalid/?api_key=embedded-secret","password":"password-secret","nested":{"access_token":"nested-secret"}}""");
            using var parsed = JsonDocument.Parse(redacted);
            Check(!redacted.Contains("embedded-secret", StringComparison.Ordinal) && !redacted.Contains("password-secret", StringComparison.Ordinal)
                && !redacted.Contains("nested-secret", StringComparison.Ordinal) && !redacted.Contains("userinfo-secret", StringComparison.Ordinal), "user operation inputs are redacted while retaining valid structured JSON");
        }
        finally { OperationDiagnostics.Written -= Record; }
    }

    private sealed class DiagnosticHandler : HttpMessageHandler
    {
        public bool StreamFailure { get; set; }
        public bool Success { get; set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(StreamFailure || Success ? HttpStatusCode.OK : HttpStatusCode.BadRequest)
            {
                Content = StreamFailure ? new StreamContent(new DiagnosticBrokenStream()) : new StringContent(Success ? """{"result":"synthetic successful response"}""" : """{"error":{"message":"unsupported encoding_format; header-secret","api_key":"provider-token-secret"}}""", Encoding.UTF8, "application/json")
            };
            if (StreamFailure) response.Content.Headers.ContentType = new("text/event-stream");
            response.Headers.TryAddWithoutValidation("x-request-id", "synthetic-provider-request");
            response.Headers.TryAddWithoutValidation("Set-Cookie", "cookie-secret");
            return Task.FromResult(response);
        }
    }

    private sealed class DiagnosticBrokenStream : MemoryStream
    {
        private bool read;
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (read) return ValueTask.FromException<int>(new IOException("Synthetic interrupted response header-secret", new IOException("Synthetic underlying socket failure")));
            read = true;
            buffer.Span[..2].Fill(42);
            return ValueTask.FromResult(2);
        }
    }
}
