using System.Diagnostics;
using System.Text;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

namespace OhMyStudy.Infrastructure.Grading;

/// <summary>Coalesces public output deltas into bounded durable snapshots, rather than one write per token.</summary>
internal static class ModelActivity
{
    private static readonly ProviderRetryPolicy RetryPolicy = new() { MaximumAttempts = 4 };

    public static async Task<(Turn Turn, int Retries)> CallAsync(JobContext context, uint ordinal, Func<Action<ProviderProgress>, Task<Turn>> call, CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var activity = new JobActivity { Key = $"model-{ordinal}", Kind = "model", Title = $"Recognition round {ordinal}", Detail = "Waiting for the model response" };
        await context.ActivityAsync(activity).ConfigureAwait(false);
        object gate = new();
        var text = new StringBuilder();
        string detail = activity.Detail;
        bool dirty = false;
        void Progress(ProviderProgress progress)
        {
            lock (gate)
            {
                if (progress.Text is { } delta && text.Length < 16000)
                    text.Append(delta.AsSpan(0, Math.Min(delta.Length, 16000 - text.Length)));
                detail = progress.Status ?? (progress.Thinking ? "Model is reasoning" : progress.ToolName is not null ? "Preparing the next action" : "Receiving the model response");
                dirty = true;
            }
        }
        using var stop = new CancellationTokenSource();
        async Task Pump()
        {
            try
            {
                while (true)
                {
                    await Task.Delay(400, stop.Token).ConfigureAwait(false);
                    JobActivity snapshot;
                    lock (gate)
                    {
                        if (!dirty) continue;
                        dirty = false;
                        snapshot = activity with { Text = text.ToString(), Detail = detail, ElapsedMs = clock.ElapsedMilliseconds };
                    }
                    await context.ActivityAsync(snapshot).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        var pump = Pump();
        Turn turn;
        int retries = 0;
        try
        {
            for (int attempt = 1; ; attempt++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try { turn = await call(Progress).ConfigureAwait(false); break; }
                catch (Exception error) when (attempt < RetryPolicy.MaximumAttempts && !cancellationToken.IsCancellationRequested && CanRetry(error))
                {
                    retries++;
                    var delay = ProviderClient.RetryDelay((error as ProviderException)?.Error.RetryAfter, attempt, RetryPolicy);
                    string partial;
                    lock (gate)
                    {
                        partial = text.ToString();
                        text.Clear();
                        detail = "Connection interrupted. Retrying…";
                        dirty = true;
                    }
                    OperationDiagnostics.Record("Model.Retry", new()
                    {
                        ["job_id"] = context.JobId,
                        ["round"] = ordinal,
                        ["attempt"] = attempt,
                        ["next_attempt"] = attempt + 1,
                        ["maximum_attempts"] = RetryPolicy.MaximumAttempts,
                        ["delay_ms"] = delay.TotalMilliseconds,
                        ["partial_text"] = partial,
                        ["usage_unknown"] = true,
                        ["exception"] = error.ToString()
                    }, true);
                    await context.ActivityAsync(activity with { Detail = detail, ElapsedMs = clock.ElapsedMilliseconds }).ConfigureAwait(false);
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
            }
        }
        catch (Exception error) when (cancellationToken.IsCancellationRequested)
        {
            OperationDiagnostics.Record("Model.Cancelled", new() { ["job_id"] = context.JobId, ["round"] = ordinal, ["retries"] = retries, ["cancelled_by_user"] = context.Cancellation.IsCancellationRequested, ["exception"] = error.ToString() });
            throw;
        }
        catch (Exception error) when (!context.Cancellation.IsCancellationRequested)
        {
            OperationDiagnostics.Record("Model.Failed", new() { ["job_id"] = context.JobId, ["round"] = ordinal, ["retries"] = retries, ["exception"] = error.ToString() }, true);
            lock (gate)
            {
                activity = activity with { State = "failed" };
                detail = FailureDetail(error);
                dirty = true;
            }
            throw;
        }
        finally
        {
            await stop.CancelAsync().ConfigureAwait(false);
            await pump.ConfigureAwait(false);
            // Preserve even a short partial reply when transport fails before the first timer tick.
            JobActivity? last = null;
            lock (gate)
                if (dirty) last = activity with { Text = text.ToString(), Detail = detail, ElapsedMs = clock.ElapsedMilliseconds };
            if (last is not null && !context.Cancellation.IsCancellationRequested)
                await context.ActivityAsync(last).ConfigureAwait(false);
        }
        string output = turn.Text.Length > 16000 ? turn.Text[..16000] : turn.Text;
        await context.ActivityAsync(activity with
        {
            State = "completed",
            Text = output,
            ElapsedMs = clock.ElapsedMilliseconds,
            Detail = turn.Calls.Count > 0 ? $"Actions prepared: {turn.Calls.Count}" : "Model response received"
        }).ConfigureAwait(false);
        OperationDiagnostics.Record("Model.Completed", new()
        {
            ["job_id"] = context.JobId,
            ["round"] = ordinal,
            ["retries"] = retries,
            ["elapsed_ms"] = clock.ElapsedMilliseconds,
            ["tool_calls"] = turn.Calls.Count,
            ["text"] = turn.Text
        });
        return (turn, retries);
    }

    private static bool CanRetry(Exception error) => error switch
    {
        ProviderException { Kind: ErrorKind.Network or ErrorKind.Server or ErrorKind.RateLimit } => true,
        ProviderException { Kind: ErrorKind.Incomplete, Diagnostic: "stream_interrupted" } => true,
        HttpRequestException or TaskCanceledException => true,
        IOException => true,
        _ => false
    };

    private static string FailureDetail(Exception error) => error switch
    {
        ProviderException { Diagnostic: "files_api_rejected" } => "The provider rejected a Files API operation. Check Support Files API and the provider's file permissions or expiration support.",
        ProviderException { Diagnostic: "request_too_large" } => "The request is too large. Enable Support Files API if the provider supports it, or recognize fewer pages at a time.",
        ProviderException { Diagnostic: "thinking_effort_rejected" } => "The provider does not support this reasoning effort. Check model settings and retry.",
        ProviderException { Diagnostic: "thinking_budget_rejected" or "thinking_configuration_rejected" } => "The provider rejected the reasoning settings. Check supported options and retry.",
        ProviderException { Diagnostic: "output_token_limit_rejected" } => "The output limit exceeds the model's limit. Update model settings and retry.",
        ProviderException { Kind: ErrorKind.Authentication } => "Model authentication failed. Check the key and model access permissions.",
        ProviderException { Kind: ErrorKind.Quota } => "Provider quota exhausted. Check your account balance and retry.",
        ProviderException { Kind: ErrorKind.RateLimit } => "Provider rate limit reached. Try again later.",
        ProviderException { Kind: ErrorKind.Network } or HttpRequestException or IOException => "Model connection lost or timed out. Check the network and retry.",
        ProviderException { Kind: ErrorKind.Refusal } => "The model refused this request. Check the materials and retry.",
        ProviderException { Kind: ErrorKind.Incomplete } => "Model output is incomplete and may have reached its limit. Lower reasoning effort or recognize smaller batches.",
        ProviderException { Kind: ErrorKind.InvalidRequest or ErrorKind.UnsupportedParameter } => "The provider rejected the task. Check the model, parameters, and endpoint address.",
        ProviderException { Kind: ErrorKind.Server } => "The model service is temporarily unavailable. Try again later.",
        OperationCanceledException => "Recognition timed out. Retry later or use smaller batches.",
        _ => "The model call did not complete. Check model settings and retry."
    };
}
