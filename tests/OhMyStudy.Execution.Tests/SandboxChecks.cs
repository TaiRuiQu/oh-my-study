using System.Text.Json;
using System.Xml.Linq;
using OhMyStudy.Application.Execution;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Execution;

internal static class SandboxChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check, Action<bool, string> require)
    {
        var runtime = Path.Combine(root, "sandbox-runtime"); Directory.CreateDirectory(runtime);
        foreach (var name in new[] { "python.exe", "execute.py", "sandbox-worker.py" }) await File.WriteAllTextAsync(Path.Combine(runtime, name), "synthetic");
        var input = Path.Combine(root, "sandbox-input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "资料.txt"), "合成输入");
        ExecutionCall Call(string id) => new()
        {
            ProtocolVersion = 1,
            JobId = id,
            CallId = "call-1",
            Tool = ToolKind.Python,
            Content = "print('你好')",
            Arguments = ["中文", "a b"],
            WorkingDirectory = input,
            TimeoutMs = 5000
        };
        ExecutionPolicy Policy(bool network = false) => ExecutionPolicy.Freeze(ExecutionMode.Sandbox, new() { State = CapabilityState.Ready, Reason = "synthetic" }, network);
        await check("sandbox-owned-id-minimal-mappings-and-post-stop-result", async () =>
        {
            using var broker = new PermissionBroker(); broker.RegisterJob("sandbox-good", Policy());
            var control = new SyntheticSandbox { MutateOnStop = true };
            var result = await new SandboxToolExecutor(runtime, control).ExecuteAsync(broker.Request(Call("sandbox-good"))!, Path.Combine(root, "sandbox-good"), CancellationToken.None);
            require(result.Succeeded && result.Stdout == "after-stop" && control.Started == control.Stopped && control.Started != Guid.Empty, "ownership / read-before-stop");
            var xml = XDocument.Parse(control.Configuration!);
            var mappings = xml.Descendants("MappedFolder").ToArray();
            require(mappings.Length == 4 && mappings.Take(3).All(m => m.Element("ReadOnly")!.Value == "true") && mappings[3].Element("ReadOnly")!.Value == "false", "minimal mapping write scope");
            require(xml.Descendants("Networking").Single().Value == "Disable" && !xml.Descendants("LogonCommand").Any(), "network/default duplicate worker");
            require(File.ReadAllText(Path.Combine(root, "sandbox-good", "inputs", "资料.txt")) == "合成输入", "input snapshot");
            require(File.ReadAllText(Path.Combine(root, "sandbox-good", "ownership.json")).Contains("stopped"), "ownership receipt");
        });
        await check("sandbox-busy-does-not-adopt-stop-or-replay", async () =>
        {
            using var broker = new PermissionBroker(); broker.RegisterJob("sandbox-busy", Policy());
            var control = new SyntheticSandbox { Busy = true }; var host = new CountingExecutor();
            var authorization = broker.Request(Call("sandbox-busy"))!;
            try { await new PolicyToolExecutor(host, new SandboxToolExecutor(runtime, control)).ExecuteAsync(authorization, Path.Combine(root, "sandbox-busy"), CancellationToken.None); throw new Exception("busy accepted"); }
            catch (IOException) { }
            require(control.Started == Guid.Empty && control.Stopped == Guid.Empty && host.Calls == 0, "existing sandbox adopted/stopped/fallback");
            try { authorization.Consume(); throw new Exception("failed sandbox authorization replayed"); } catch (InvalidOperationException) { }
        });
        await check("sandbox-rejects-wrong-nonce-and-oversized-response", async () =>
        {
            foreach (var mode in new[] { "nonce", "output", "identity", "status", "json", "null", "exit" })
            {
                var id = "sandbox-bad-" + mode;
                using var broker = new PermissionBroker(); broker.RegisterJob(id, Policy());
                var control = new SyntheticSandbox { Corrupt = mode };
                try { await new SandboxToolExecutor(runtime, control).ExecuteAsync(broker.Request(Call(id))!, Path.Combine(root, id), CancellationToken.None); throw new Exception("bad result accepted: " + mode); }
                catch (InvalidDataException) { }
                require(control.Started == control.Stopped && control.Stopped != Guid.Empty, "invalid bridge did not stop exactly owned id");
            }
        });
        await check("sandbox-cancel-revocation-start-failure-and-network-freeze", async () =>
        {
            foreach (var mode in new[] { "cancel", "revoke", "start-fail" })
            {
                var id = "sandbox-" + mode;
                using var broker = new PermissionBroker(); broker.RegisterJob(id, Policy(true));
                var control = new SyntheticSandbox { HoldResponse = true, FailStart = mode == "start-fail" };
                using var cancel = new CancellationTokenSource();
                var operation = new SandboxToolExecutor(runtime, control).ExecuteAsync(broker.Request(Call(id))!, Path.Combine(root, id), cancel.Token);
                if (mode != "start-fail")
                {
                    await control.WorkerStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
                    if (mode == "cancel") cancel.Cancel(); else broker.Close(id);
                }
                try { await operation; throw new Exception("cancel/failure accepted"); }
                catch (OperationCanceledException) when (mode != "start-fail") { }
                catch (IOException) when (mode == "start-fail") { }
                require(control.Started == control.Stopped && !control.CleanupWasCancelled, "cleanup lost to cancellation");
                require(XDocument.Parse(control.Configuration!).Descendants("Networking").Single().Value == "Enable", "frozen networking lost");
            }
        });
        await check("sandbox-input-snapshot-limits", async () =>
        {
            var tooMany = Path.Combine(root, "sandbox-many-inputs"); Directory.CreateDirectory(tooMany);
            for (int i = 0; i < 257; i++) await File.WriteAllTextAsync(Path.Combine(tooMany, i + ".txt"), "x");
            using var broker = new PermissionBroker(); broker.RegisterJob("sandbox-limit", Policy());
            var control = new SyntheticSandbox();
            try { await new SandboxToolExecutor(runtime, control).ExecuteAsync(broker.Request(Call("sandbox-limit") with { WorkingDirectory = tooMany })!, Path.Combine(root, "sandbox-limit"), CancellationToken.None); throw new Exception("unbounded input snapshot"); }
            catch (IOException) { }
            require(control.Started == Guid.Empty && control.Stopped == Guid.Empty, "started before input validation");
        });
        // Last sandbox check: a failed stop deliberately latches the process against any further VM creation.
        await check("sandbox-cleanup-failure-blocks-later-launch", async () =>
        {
            using var broker = new PermissionBroker(); broker.RegisterJob("sandbox-stop-fail", Policy()); broker.RegisterJob("sandbox-after-fail", Policy());
            var control = new SyntheticSandbox { FailStop = true }; var executor = new SandboxToolExecutor(runtime, control);
            try { await executor.ExecuteAsync(broker.Request(Call("sandbox-stop-fail"))!, Path.Combine(root, "sandbox-stop-fail"), CancellationToken.None); throw new Exception("cleanup failure accepted"); }
            catch (IOException error) { require(error.Message.Contains("手动关闭"), "missing recovery action"); }
            try { await executor.ExecuteAsync(broker.Request(Call("sandbox-after-fail"))!, Path.Combine(root, "sandbox-after-fail"), CancellationToken.None); throw new Exception("start after failed cleanup"); }
            catch (IOException) { }
            require(control.Starts == 1, "new VM after uncertain teardown");
        });
    }
}

internal sealed class SyntheticSandbox : ISandboxControl
{
    public bool Busy, MutateOnStop, HoldResponse, FailStart, FailStop;
    public string? Corrupt, Configuration;
    public Guid Started, Stopped;
    public int Starts;
    public bool CleanupWasCancelled;
    private string? _results;
    private SandboxMessage? _message;
    public TaskCompletionSource WorkerStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public Task<bool> HasSessionsAsync(CancellationToken cancellationToken) => Task.FromResult(Busy);
    public Task StartAsync(Guid id, string configuration, CancellationToken cancellationToken)
    {
        Started = id; Starts++; Configuration = configuration;
        if (FailStart) throw new IOException("synthetic ambiguous start failure");
        return Task.CompletedTask;
    }
    public async Task RunWorkerAsync(Guid id, string jobId, string nonce, CancellationToken cancellationToken)
    {
        var mappings = XDocument.Parse(Configuration!).Descendants("MappedFolder").ToArray();
        var requests = mappings.Single(m => m.Element("SandboxFolder")!.Value.EndsWith("Requests")).Element("HostFolder")!.Value;
        _results = mappings.Single(m => m.Element("SandboxFolder")!.Value.EndsWith("Results")).Element("HostFolder")!.Value;
        var request = JsonSerializer.Deserialize(await File.ReadAllTextAsync(Path.Combine(requests, "request.json"), cancellationToken), SandboxJson.Default.SandboxRequest)!;
        var message = new SandboxMessage(1, Corrupt == "identity" ? "wrong-job" : jobId, request.CallId, Corrupt == "nonce" ? "wrong" : nonce, request.Digest, "ready", null, "", "");
        await WriteAsync("handshake.json", message, cancellationToken); WorkerStarted.TrySetResult();
        if (HoldResponse) { await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken); return; }
        _message = message with
        {
            Status = Corrupt == "status" ? "replay_on_host" : "completed",
            ExitCode = Corrupt == "exit" ? 7u : 0,
            Stdout = Corrupt == "null" ? null! : Corrupt == "output" ? new string('x', 2_097_153) : "before-stop"
        };
        if (Corrupt == "json")
        {
            await File.WriteAllTextAsync(Path.Combine(_results, "response.pending"), "{", cancellationToken);
            File.Move(Path.Combine(_results, "response.pending"), Path.Combine(_results, "response.json")); return;
        }
        await WriteAsync("response.json", _message, cancellationToken);
    }
    public async Task StopAsync(Guid id, CancellationToken cancellationToken)
    {
        Stopped = id; CleanupWasCancelled = cancellationToken.IsCancellationRequested;
        if (FailStop) throw new IOException("synthetic stop failure");
        if (MutateOnStop) await WriteAsync("response.json", _message! with { Stdout = "after-stop" }, cancellationToken);
    }
    private async Task WriteAsync(string name, SandboxMessage message, CancellationToken token)
    {
        var pending = Path.Combine(_results!, Guid.NewGuid().ToString("N") + ".pending");
        await File.WriteAllTextAsync(pending, JsonSerializer.Serialize(message, SandboxJson.Default.SandboxMessage), token);
        File.Move(pending, Path.Combine(_results!, name), true);
    }
}
