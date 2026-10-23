using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Diagnostics;

namespace OhMyStudy.Infrastructure.Diagnostics;

public sealed class DiagnosticHttpHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (!OperationDiagnostics.IsEnabled) return await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
        string requestId = Guid.NewGuid().ToString("N");
        string correlationId = OperationDiagnostics.CorrelationId ?? requestId;
        var clock = Stopwatch.StartNew();
        var secrets = request.Headers.Where(header => DiagnosticRedaction.IsSecret(header.Key)).SelectMany(header => header.Value)
            .Select(value => value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase) ? value[7..] : value).ToList();
        JsonObject Fields() => new() { ["request_id"] = requestId, ["correlation_id"] = correlationId, ["elapsed_ms"] = clock.ElapsedMilliseconds };
        void Log(string phase, JsonObject fields, bool failed = false) => OperationDiagnostics.Record(phase, fields, failed);
        var requestFields = new JsonObject
        {
            ["method"] = request.Method.Method,
            ["url"] = DiagnosticRedaction.Text(request.RequestUri?.AbsoluteUri ?? "", secrets),
            ["headers"] = Headers(request.Headers, secrets)
        };
        try
        {
            if (request.Content is { } content)
            {
                requestFields["content_headers"] = Headers(content.Headers, secrets);
                requestFields["body_bytes"] = content.Headers.ContentLength;
                requestFields["body"] = content is MultipartContent ? "[multipart file content omitted]"
                    : DiagnosticRedaction.Body(await content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), secrets);
            }
            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            try
            {
                var fields = Fields();
                fields["method"] = request.Method.Method;
                fields["url"] = requestFields["url"]?.DeepClone();
                fields["status"] = (int)response.StatusCode;
                fields["request_bytes"] = requestFields["body_bytes"]?.DeepClone();
                if (!response.IsSuccessStatusCode)
                {
                    fields["request"] = requestFields.DeepClone();
                    fields["reason"] = response.ReasonPhrase;
                    fields["headers"] = Headers(response.Headers, secrets);
                    fields["content_headers"] = Headers(response.Content.Headers, secrets);
                    await response.Content.LoadIntoBufferAsync(16 * 1024 * 1024, cancellationToken).ConfigureAwait(false);
                    fields["body"] = DiagnosticRedaction.Body(await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false), secrets);
                    Log("Http.Rejected", fields, true);
                }
                else
                {
                    Log("Http.Response", fields);
                    response.Content = new TracedContent(response.Content, (phase, bytes, error) =>
                    {
                        if (error is null || error is OperationCanceledException && cancellationToken.IsCancellationRequested) return;
                        var bodyFields = Fields();
                        bodyFields["request"] = requestFields.DeepClone();
                        bodyFields["bytes_received"] = bytes;
                        bodyFields["exception"] = DiagnosticRedaction.Text(error.ToString(), secrets);
                        Log(phase, bodyFields, true);
                    });
                }
                return response;
            }
            catch { response.Dispose(); throw; }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            var fields = Fields();
            fields["request"] = requestFields.DeepClone();
            fields["cancelled"] = cancellationToken.IsCancellationRequested;
            fields["exception"] = DiagnosticRedaction.Text(error.ToString(), secrets);
            Log("Http.Failed", fields, true);
            throw;
        }
    }

    private static JsonObject Headers(HttpHeaders headers, IReadOnlyList<string> secrets)
    {
        var result = new JsonObject();
        foreach (var header in headers)
            result[header.Key] = DiagnosticRedaction.IsSecret(header.Key) ? "[REDACTED]" : DiagnosticRedaction.Text(string.Join(", ", header.Value), secrets);
        return result;
    }

    private sealed class TracedContent : HttpContent
    {
        private readonly HttpContent inner;
        private readonly Action<string, long, Exception?> trace;
        private readonly long? contentLength;
        private long received;
        private int ended;

        public TracedContent(HttpContent inner, Action<string, long, Exception?> trace)
        {
            this.inner = inner; this.trace = trace;
            contentLength = inner.Headers.ContentLength;
            foreach (var header in inner.Headers) Headers.TryAddWithoutValidation(header.Key, header.Value);
        }

        private void End(string phase, Exception? error = null)
        {
            if (Interlocked.Exchange(ref ended, 1) == 0) trace(phase, Interlocked.Read(ref received), error);
        }

        private Stream Track(Stream stream) => new TracedStream(stream, count =>
        {
            if (count == 0) End("Http.BodyCompleted"); else Interlocked.Add(ref received, count);
        }, error => End("Http.BodyFailed", error));

        protected override async Task<Stream> CreateContentReadStreamAsync() => Track(await inner.ReadAsStreamAsync().ConfigureAwait(false));
        protected override async Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) => Track(await inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);
        protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
        {
            using var source = Track(await inner.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false));
            await source.CopyToAsync(stream, cancellationToken).ConfigureAwait(false);
        }

        protected override bool TryComputeLength(out long length) { length = contentLength ?? 0; return contentLength is not null; }
        protected override void Dispose(bool disposing)
        {
            if (disposing) { End("Http.BodyClosed"); inner.Dispose(); }
            base.Dispose(disposing);
        }
    }

    private sealed class TracedStream(Stream inner, Action<int> read, Action<Exception> failed) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count)
        {
            try { int result = inner.Read(buffer, offset, count); read(result); return result; }
            catch (Exception error) { failed(error); throw; }
        }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            try { int result = await inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false); read(result); return result; }
            catch (Exception error) { failed(error); throw; }
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
