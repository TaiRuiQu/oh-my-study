using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Execution;

public static class SandboxCapability
{
    public static Capability Detect()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return new() { State = CapabilityState.Unsupported, Reason = "此系统不支持 AppContainer 运行" };
        return new() { State = CapabilityState.Unknown, Reason = "尚未检查 AppContainer 能否运行" };
    }

    public static async Task<Capability> ProbeAsync(string privateRuntime, CancellationToken cancellationToken = default)
    {
        var detected = Detect();
        if (detected.State != CapabilityState.Unknown) return detected;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RuntimePaths.CheckedFile(Path.Combine(privateRuntime, "python.exe"));
            RuntimePaths.CheckedFile(Path.Combine(privateRuntime, "execute.py"));
            RuntimePaths.CheckedFile(Path.Combine(privateRuntime, "sandbox-worker.py"));
            return await Task.Run(() =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                using var profile = AppContainerProfile.Create(Guid.NewGuid());
                return new Capability { State = CapabilityState.Ready, Reason = "AppContainer 可用" };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { return new() { State = CapabilityState.Unknown, Reason = "AppContainer 暂时不可用，请检查后重试" }; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new() { State = CapabilityState.Unknown, Reason = "沙箱检查超时，请稍后重试" }; }
    }
}

public enum SandboxLifecycle { NotStarted, WaitingHandshake, Ready, Interrupted }

public sealed class SandboxSession
{
    public string JobId { get; }
    public string Nonce { get; }
    public SandboxLifecycle State { get; private set; }
    public string? InFlight { get; private set; }
    public SandboxSession(string jobId, string nonce)
    {
        if (!ExecutionCall.ValidId(jobId) || !ExecutionCall.ValidId(nonce)) throw new ArgumentException("无效的会话标识。");
        JobId = jobId; Nonce = nonce;
    }
    public void AwaitHandshake()
    {
        if (State != SandboxLifecycle.NotStarted) throw new InvalidOperationException("会话已使用。");
        State = SandboxLifecycle.WaitingHandshake;
    }
    public void Handshake(uint version, string jobId, string nonce)
    {
        if (State != SandboxLifecycle.WaitingHandshake || version != 1 || jobId != JobId || nonce != Nonce)
            throw new InvalidDataException("会话校验失败。");
        State = SandboxLifecycle.Ready;
    }
    public void BeginCall(string id)
    {
        if (State != SandboxLifecycle.Ready || InFlight is not null || !ExecutionCall.ValidId(id)) throw new InvalidOperationException("沙箱未就绪。");
        InFlight = id;
    }
    public void Complete(string id)
    {
        if (State != SandboxLifecycle.Ready || InFlight != id) throw new InvalidDataException("调用结果不匹配。");
        InFlight = null;
    }
    public string? Disconnect() { State = SandboxLifecycle.Interrupted; var lost = InFlight; InFlight = null; return lost; }
}
