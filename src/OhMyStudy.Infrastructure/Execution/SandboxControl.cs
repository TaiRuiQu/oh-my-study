using System.Text.Json;
using System.Diagnostics;
using System.Text;
using OhMyStudy.Application.Jobs;

namespace OhMyStudy.Infrastructure.Execution;

/// <summary>The implementation must create the supplied NEW id, and stop only that exact id.</summary>
public interface ISandboxControl
{
    Task<bool> HasSessionsAsync(CancellationToken cancellationToken);
    Task StartAsync(Guid id, string configuration, CancellationToken cancellationToken);
    Task RunWorkerAsync(Guid id, string jobId, string nonce, CancellationToken cancellationToken);
    Task StopAsync(Guid id, CancellationToken cancellationToken);
}

/// <summary>Uses the documented Windows 11 24H2 CLI. Never launches the legacy singleton client.</summary>
public sealed class SandboxCliControl(string executable) : ISandboxControl
{
    public async Task<bool> HasSessionsAsync(CancellationToken cancellationToken)
    {
        var result = await RunAsync(["list", "--raw"], TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
        using var json = JsonDocument.Parse(result.Stdout, new() { MaxDepth = 8 });
        // Unknown CLI formats fail closed. In particular, never infer absence from an error object.
        if (json.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("无法确认沙箱是否正在使用。");
        return json.RootElement.GetArrayLength() != 0;
    }
    public Task StartAsync(Guid id, string configuration, CancellationToken cancellationToken) =>
        RunCheckedAsync(["start", "--id", id.ToString("D"), "--config", configuration, "--raw"], TimeSpan.FromMinutes(2), cancellationToken);
    public Task RunWorkerAsync(Guid id, string jobId, string nonce, CancellationToken cancellationToken)
    {
        if (!OhMyStudy.Domain.ExecutionCall.ValidId(jobId) || !OhMyStudy.Domain.ExecutionCall.ValidId(nonce)) throw new ArgumentException("无效的会话标识。");
        var command = $@"C:\OMS\Runtime\python.exe -I -X utf8 C:\OMS\Runtime\sandbox-worker.py --job-id {jobId} --nonce {nonce}";
        return RunCheckedAsync(["exec", "--id", id.ToString("D"), "--command", command, "--run-as", "System", "--raw"], TimeSpan.FromHours(1), cancellationToken);
    }
    public Task StopAsync(Guid id, CancellationToken cancellationToken) =>
        RunCheckedAsync(["stop", "--id", id.ToString("D"), "--raw"], TimeSpan.FromSeconds(30), cancellationToken);
    private async Task RunCheckedAsync(string[] arguments, TimeSpan timeout, CancellationToken token) =>
        _ = await RunAsync(arguments, timeout, token).ConfigureAwait(false);
    private async Task<ToolExecutionResult> RunAsync(string[] arguments, TimeSpan timeout, CancellationToken token)
    {
        var system = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var allowed = SandboxCapability.FindCli();
        if (allowed is null || !Path.GetFullPath(executable).Equals(allowed, StringComparison.OrdinalIgnoreCase))
            throw new IOException("沙箱运行组件不可用。");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = system,
            StandardOutputEncoding = Encoding.UTF8, StandardErrorEncoding = Encoding.UTF8 };
        start.Environment.Clear();
        foreach (var pair in WindowsProcessRunner.CreateEnvironment(Path.GetDirectoryName(executable)!, Path.GetTempPath())) start.Environment[pair.Key] = pair.Value;
        // The Store app execution alias requires the current identity, but no provider secrets.
        foreach (var name in new[] { "LOCALAPPDATA", "USERPROFILE" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = new Process { StartInfo = start };
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token);
        limit.CancelAfter(timeout);
        int remaining = 65_536;
        async Task<string> CaptureAsync(StreamReader reader)
        {
            var text = new StringBuilder(); var buffer = new char[1024];
            while (true)
            {
                int count = await reader.ReadAsync(buffer.AsMemory(), limit.Token).ConfigureAwait(false);
                if (count == 0) return text.ToString();
                if (Interlocked.Add(ref remaining, -count) < 0) { limit.Cancel(); throw new IOException("沙箱返回内容过多。"); }
                text.Append(buffer, 0, count);
            }
        }
        token.ThrowIfCancellationRequested();
        process.Start();
        var output = CaptureAsync(process.StandardOutput); var stderrTask = CaptureAsync(process.StandardError);
        try
        {
            await process.WaitForExitAsync(limit.Token).ConfigureAwait(false);
            var logs = await Task.WhenAll(output, stderrTask).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new IOException("沙箱操作未完成，请检查沙箱状态后重试。");
            return new("completed", 0, logs[0], logs[1]);
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new IOException("沙箱操作超时或返回内容过多，请检查沙箱后重试。", error); }
        finally
        {
            // Kill only the CLI we launched. The server is released by exact Sandbox ID, never PID/name.
            limit.Cancel();
            if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            try { await Task.WhenAll(output, stderrTask).ConfigureAwait(false); } catch (Exception) { /* Observe cancelled readers. */ }
        }
    }
}
