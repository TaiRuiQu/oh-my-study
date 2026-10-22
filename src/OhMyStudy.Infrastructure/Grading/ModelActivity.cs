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
                detail = progress.Thinking ? "模型正在思考" : progress.ToolName is not null ? "正在准备下一步操作" : "正在接收模型响应";
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
        catch (Exception error) when (!context.Cancellation.IsCancellationRequested)
        {
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
            Detail = turn.Calls.Count > 0 ? $"已生成 {turn.Calls.Count} 项操作" : "已收到模型响应"
        }).ConfigureAwait(false);
        return turn;
    }

    private static string FailureDetail(Exception error) => error switch
    {
        ProviderException { Diagnostic: "thinking_effort_rejected" } => "服务商不支持所选思考强度，请检查模型配置后重试。",
        ProviderException { Diagnostic: "thinking_budget_rejected" or "thinking_configuration_rejected" } => "服务商拒绝了思考设置，请检查模型支持的思考方式后重试。",
        ProviderException { Diagnostic: "output_token_limit_rejected" } => "输出上限超出模型限制，请更新模型配置后重试。",
        ProviderException { Kind: ErrorKind.Authentication } => "模型鉴权失败，请检查密钥和模型访问权限后重试。",
        ProviderException { Kind: ErrorKind.Quota } => "服务商额度不足，请检查账户余额后重试。",
        ProviderException { Kind: ErrorKind.RateLimit } => "服务商请求过于频繁，请稍后重试。",
        ProviderException { Kind: ErrorKind.Network } or HttpRequestException or IOException => "模型连接中断或超时，请检查网络后重试。",
        ProviderException { Kind: ErrorKind.Refusal } => "模型拒绝了本次请求，请检查资料后重试。",
        ProviderException { Kind: ErrorKind.Incomplete } => "模型输出未完成，可能已达到输出上限，请降低思考强度或分批识别。",
        ProviderException { Kind: ErrorKind.InvalidRequest or ErrorKind.UnsupportedParameter } => "服务商拒绝了任务请求，请检查模型、参数和接口地址后重试。",
        ProviderException { Kind: ErrorKind.Server } => "模型服务暂不可用，请稍后重试。",
        OperationCanceledException => "识别已超时，请稍后重试或分批识别。",
        _ => "模型调用未完成，请检查模型设置后重试。"
    };
}
