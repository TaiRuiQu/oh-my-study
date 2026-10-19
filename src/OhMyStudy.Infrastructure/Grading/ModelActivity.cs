using System.Diagnostics;
using System.Text;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

namespace OhMyStudy.Infrastructure.Grading;

/// <summary>Coalesces public output deltas into bounded durable snapshots, rather than one write per token.</summary>
internal static class ModelActivity
{
    public static async Task<Turn> CallAsync(JobContext context, uint ordinal, Func<Action<ProviderProgress>, Task<Turn>> call)
    {
        var clock = Stopwatch.StartNew();
        var activity = new JobActivity { Key = $"model-{ordinal}", Kind = "model", Title = $"第 {ordinal} 轮识别", Detail = "正在等待模型响应" };
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
                detail = progress.ToolName is not null ? "正在准备下一步操作" : "正在接收模型响应";
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
        try { turn = await call(Progress).ConfigureAwait(false); }
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
            Detail = turn.Calls.Count > 0 ? $"已生成 {turn.Calls.Count} 项操作" : "已收到模型响应"
        }).ConfigureAwait(false);
        return turn;
    }
}
