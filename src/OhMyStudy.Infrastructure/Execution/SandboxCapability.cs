using System.Diagnostics;
using Microsoft.Win32;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Execution;

/// <summary>Read-only detection. Never enables Windows features or acquires another user's session.</summary>
public static class SandboxCapability
{
    public static string? FindCli()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 26100)) return null;
        foreach (var path in new[] {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "wsb.exe"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "wsb.exe") })
            if (File.Exists(path)) return Path.GetFullPath(path);
        return null;
    }

    public static Capability Detect()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return new() { State = CapabilityState.Unsupported, Reason = "此系统不支持沙箱运行" };
        try
        {
            using var current = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion");
            var edition = current?.GetValue("EditionID") as string ?? "";
            if (edition.StartsWith("Core", StringComparison.OrdinalIgnoreCase) || edition.Contains("Server", StringComparison.OrdinalIgnoreCase))
                return new() { State = CapabilityState.Unsupported, Reason = "此Windows版本不提供沙箱" };
            foreach (var name in new[] { "WindowsSandbox", "WindowsSandboxClient", "WindowsSandboxRemoteSession" })
            {
                var processes = Process.GetProcessesByName(name);
                try
                {
                    if (processes.Length > 0) return new() { State = CapabilityState.Busy, Reason = "沙箱正在使用中" };
                }
                finally { foreach (var process in processes) process.Dispose(); }
            }
            string executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsSandbox.exe");
            if (!File.Exists(executable)) return new() { State = CapabilityState.NeedsEnable, Reason = "此电脑尚未准备好沙箱" };
            if (FindCli() is null) return new() { State = CapabilityState.Unknown, Reason = "请更新Windows和沙箱后重试" };
            // This synchronous, read-only probe is deliberately conservative. ProbeAsync also verifies CLI availability.
            return new() { State = CapabilityState.Unknown, Reason = "尚未检查沙箱能否运行" };
        }
        catch (Exception error) when (error is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        { return new() { State = CapabilityState.Unknown, Reason = "暂时无法确认沙箱是否可用" }; }
    }

    public static async Task<Capability> ProbeAsync(string privateRuntime, CancellationToken cancellationToken = default)
    {
        var detected = Detect();
        if (detected.State != CapabilityState.Unknown) return detected;
        try
        {
            var cli = FindCli();
            if (cli is null) return detected;
            RuntimePaths.CheckedFile(Path.Combine(privateRuntime, "python.exe"));
            RuntimePaths.CheckedFile(Path.Combine(privateRuntime, "execute.py"));
            RuntimePaths.CheckedFile(Path.Combine(privateRuntime, "sandbox-worker.py"));
            return await new SandboxCliControl(cli).HasSessionsAsync(cancellationToken).ConfigureAwait(false)
                ? new() { State = CapabilityState.Busy, Reason = "沙箱正在使用中" }
                : new() { State = CapabilityState.Ready, Reason = "沙箱可用" };
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { return new() { State = CapabilityState.Unknown, Reason = "沙箱暂时不可用，请检查后重试" }; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new() { State = CapabilityState.Unknown, Reason = "沙箱检查超时，请稍后重试" }; }
    }
}

public enum SandboxLifecycle { NotStarted, WaitingHandshake, Ready, Interrupted }

/// <summary>A protocol state machine, deliberately separate from OS session ownership.</summary>
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
