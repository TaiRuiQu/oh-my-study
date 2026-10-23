using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

internal static partial class Program
{
    private static async Task StreamRetryRecovery()
    {
        foreach (var protocol in new[] { Protocol.Responses, Protocol.Messages })
        {
            foreach (bool cleanEnd in new[] { false, true })
            {
                await using var fixture = await Fixture.Create(ImportMode.Blank, protocol);
                var question = Question(fixture.Document);
                var added = new List<SavedQuestion>();
                fixture.QuestionSaved = added.Add;
                int attempts = 0;
                string? historyBeforeRetry = null;
                var events = new List<(string Phase, string Detail)>();
                void Log(string phase, string detail, bool failed) => events.Add((phase, detail));
                var client = new DelegateClient(protocol, (round, history, _) =>
                {
                    attempts++;
                    if (round == 1) return Task.FromResult(NativeTurn(protocol, [.. fixture.Document.Pages.Select(page => Read(page)), Save(question)]));
                    if (round == 2)
                    {
                        historyBeforeRetry = history.ToJsonString();
                        throw cleanEnd ? ProviderProtocol.Interrupted("stream ended before terminal event") : new ProviderException(ErrorKind.Network, "synthetic stream disconnected");
                    }
                    Check(history.ToJsonString() == historyBeforeRetry, "retry preserves the completed conversation and discards partial output");
                    return Task.FromResult(NativeTurn(protocol, [Finish(Coverage(fixture.Document, question.DraftKey))]));
                });
                OperationDiagnostics.Written += Log;
                Job job;
                try { job = await fixture.Run(client); }
                finally { OperationDiagnostics.Written -= Log; }
                var result = await fixture.Library.GradingResultAsync(fixture.Document.Id);
                Check(job.State == JobState.Completed && attempts == 3 && result.Questions.Count == 1, "network failure and missing stream terminal recover without repeating tools");
                Check(added.Count == 1 && added[0].Id == result.Questions[0].Id, "each committed model question is submitted for background indexing once");
                var retried = result.ModelCalls.Single(call => call.GetProperty("retries").GetInt32() == 1);
                Check(retried.GetProperty("retry_usage_unknown").GetBoolean() && retried.GetProperty("estimated_cost").ValueKind == System.Text.Json.JsonValueKind.Null, "retry records actual attempts and leaves unreported costs unknown");
                Check(events.Any(entry => entry.Phase == "Model.Retry" && entry.Detail.Contains("synthetic", StringComparison.Ordinal) == !cleanEnd)
                    && events.Count(entry => entry.Phase == "Model.Completed") == 2
                    && events.All(entry => entry.Phase is not ("Model.Attempt" or "Tool.Start")), "logs retain model results and retry decisions without routine attempt and tool start events");
                Check(events.Where(entry => entry.Phase == "Model.Completed").All(entry => !entry.Detail.Contains("\"context\"", StringComparison.Ordinal))
                    && events.Where(entry => entry.Phase == "Tool.Completed").All(entry => !entry.Detail.Contains("\"arguments\"", StringComparison.Ordinal) && !entry.Detail.Contains("\"result\"", StringComparison.Ordinal)), "successful model and tool logs omit conversation history and repeated payloads");
            }
        }

        foreach (var kind in new[] { ErrorKind.Authentication, ErrorKind.InvalidRequest, ErrorKind.Incomplete })
        {
            await using var fixture = await Fixture.Create(ImportMode.Blank, Protocol.Responses);
            var client = new DelegateClient(Protocol.Responses, (_, _, _) => throw new ProviderException(kind, "synthetic permanent failure"));
            Check((await fixture.Run(client)).State == JobState.Failed && client.Attempts == 1, "authentication, parameters and explicit incomplete output are not retried");
        }

        await using (var fixture = await Fixture.Create(ImportMode.Blank, Protocol.Responses))
        {
            var retryStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var client = new DelegateClient(Protocol.Responses, (_, _, _) => throw new ProviderException(ErrorKind.Network, "synthetic disconnect before cancellation"));
            void Log(string phase, string detail, bool failed) { if (phase == "Model.Retry") retryStarted.TrySetResult(); }
            OperationDiagnostics.Written += Log;
            try
            {
                await using var queue = await fixture.Queue(client);
                var job = await queue.SubmitAsync(DiagnosticKind.Grading, Policy, fixture.Input);
                await retryStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
                await queue.CancelAsync(job.Id);
                var cancelled = await Wait(queue, job.Id);
                Check(cancelled.State == JobState.Cancelled && client.Attempts == 1, "cancellation interrupts retry backoff without another request");
            }
            finally { OperationDiagnostics.Written -= Log; }
        }

        foreach (var protocol in new[] { Protocol.Responses, Protocol.Messages })
        {
            await using var fixture = await Fixture.Create(ImportMode.Blank, protocol);
            var question = Question(fixture.Document);
            using var scripted = new ScriptApi(protocol, (_, _, _) => Task.FromResult<List<ToolCall>>([.. fixture.Document.Pages.Select(page => Read(page)), Save(question), Finish(Coverage(fixture.Document, question.DraftKey))]));
            using var wire = new DisconnectOnceHandler(scripted);
            using var http = new HttpClient(wire);
            using var client = new ProviderClient(http, protocol, fixture.Input.Provider.BaseUrl);
            var job = await fixture.Run(client);
            Check(job.State == JobState.Completed && wire.Attempts == 2, "actual SDK stream read failure is retried for Responses and Messages");
        }
    }

    private sealed class DisconnectOnceHandler(HttpMessageHandler next) : HttpMessageHandler
    {
        private readonly HttpMessageInvoker inner = new(next, false);
        public int Attempts { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (++Attempts > 1) return inner.SendAsync(request, cancellationToken);
            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new DisconnectedStream()) };
            response.Content.Headers.ContentType = new("text/event-stream");
            return Task.FromResult(response);
        }
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }

    private sealed class DisconnectedStream : MemoryStream
    {
        public override int Read(byte[] buffer, int offset, int count) => throw new IOException("Synthetic interrupted response body");
        public override int Read(Span<byte> buffer) => throw new IOException("Synthetic interrupted response body");
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => Task.FromException<int>(new IOException("Synthetic interrupted response body"));
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => ValueTask.FromException<int>(new IOException("Synthetic interrupted response body"));
    }
}
