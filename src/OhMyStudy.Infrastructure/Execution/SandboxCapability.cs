using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Execution;

public static class SandboxCapability
{
    public static Capability Detect()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041))
            return new() { State = CapabilityState.Unsupported, Reason = "This system does not support AppContainer execution" };
        return new() { State = CapabilityState.Unknown, Reason = "AppContainer availability has not been checked" };
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
                return new Capability { State = CapabilityState.Ready, Reason = "AppContainer is available" };
            }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or System.Text.Json.JsonException)
        { return new() { State = CapabilityState.Unknown, Reason = "AppContainer is currently unavailable. Check it and retry" }; }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new() { State = CapabilityState.Unknown, Reason = "The sandbox check timed out. Retry later" }; }
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
        if (!ExecutionCall.ValidId(jobId) || !ExecutionCall.ValidId(nonce)) throw new ArgumentException("Invalid session ID.");
        JobId = jobId; Nonce = nonce;
    }
    public void AwaitHandshake()
    {
        if (State != SandboxLifecycle.NotStarted) throw new InvalidOperationException("The session has already been used.");
        State = SandboxLifecycle.WaitingHandshake;
    }
    public void Handshake(uint version, string jobId, string nonce)
    {
        if (State != SandboxLifecycle.WaitingHandshake || version != 1 || jobId != JobId || nonce != Nonce)
            throw new InvalidDataException("Session verification failed.");
        State = SandboxLifecycle.Ready;
    }
    public void BeginCall(string id)
    {
        if (State != SandboxLifecycle.Ready || InFlight is not null || !ExecutionCall.ValidId(id)) throw new InvalidOperationException("The sandbox is not ready.");
        InFlight = id;
    }
    public void Complete(string id)
    {
        if (State != SandboxLifecycle.Ready || InFlight != id) throw new InvalidDataException("The call result does not match.");
        InFlight = null;
    }
    public string? Disconnect() { State = SandboxLifecycle.Interrupted; var lost = InFlight; InFlight = null; return lost; }
}
