using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OhMyStudy.Application.Execution;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Execution;

if (args.Length > 0 && args[0] == "--child") return await Child.RunAsync(args[1..]);
var output = args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Combine(Path.GetTempPath(), "oms-execution-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(output);
var root = Path.Combine(output, "合成 任务 " + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
var results = new List<CheckResult>();
async Task Check(string name, Func<Task> test)
{
    try { await test(); results.Add(new(name, true, null)); }
    catch (Exception error) { results.Add(new(name, false, error.GetType().Name + ": " + error.Message)); }
    Console.WriteLine($"{(results[^1].Passed ? "PASS" : "FAIL")} {name}: {results[^1].Error}");
}
void Require(bool value, string detail) { if (!value) throw new InvalidOperationException(detail); }
ExecutionPolicy Policy(ExecutionMode mode = ExecutionMode.BypassPermission) => ExecutionPolicy.Freeze(mode, new() { State = CapabilityState.Unknown, Reason = "synthetic" }, false);
ExecutionCall Call(string job, string id = "call-1", string text = "print('你好')") => new()
{
    ProtocolVersion = 1,
    JobId = job,
    CallId = id,
    Tool = ToolKind.Python,
    Content = text,
    Arguments = ["a b", "尾\\", "中文"],
    WorkingDirectory = root,
    TimeoutMs = 1000
};
async Task WaitFor(Func<Task<bool>> predicate, int timeout = 5000)
{
    var watch = Stopwatch.StartNew();
    while (!await predicate()) { if (watch.ElapsedMilliseconds > timeout) throw new TimeoutException("等待任务状态超时"); await Task.Delay(10); }
}

await Check("native-aot", () => { Require(!RuntimeFeature.IsDynamicCodeSupported, "must run native executable"); return Task.CompletedTask; });
await Check("permission-binding-consumption-and-revocation", () =>
{
    using var broker = new PermissionBroker();
    broker.RegisterJob("one", Policy(ExecutionMode.RequestPermission));
    broker.RegisterJob("two", Policy(ExecutionMode.RequestPermission));
    var call = Call("one");
    Require(broker.Request(call) is null, "permission missing");
    try { broker.Decide("one", "changed", PermissionChoice.AllowOnce); throw new Exception("changed hash accepted"); } catch (InvalidOperationException) { }
    var token = broker.Decide("one", call.Digest(), PermissionChoice.AllowOnce)!;
    call.Arguments.Add("late mutation");
    Require(token.Consume().Arguments.Count == 3, "review payload was mutable");
    try { token.Consume(); throw new Exception("replayed token"); } catch (InvalidOperationException) { }
    try { broker.Request(call); throw new Exception("replayed call id"); } catch (InvalidOperationException) { }
    var second = Call("one", "call-2"); broker.Request(second);
    var granted = broker.Decide("one", second.Digest(), PermissionChoice.AllowTask)!;
    Require(broker.Request(Call("one", "call-3")) is not null && broker.Request(Call("two")) is null, "cross-job task grant");
    broker.Close("one"); Require(!granted.IsLive, "revocation");
    try { granted.Consume(); throw new Exception("revoked token used"); } catch (OperationCanceledException) { }
    broker.Decide("two", Call("two").Digest(), PermissionChoice.Deny);
    try { broker.Request(Call("two", "reworded")); throw new Exception("denial retried"); } catch (InvalidOperationException) { }
    return Task.CompletedTask;
});
await Check("queue-serial-and-event-order", async () =>
{
    var store = new MemoryJobStore();
    int active = 0, maximum = 0;
    var runner = new DelegateRunner(async (_, context) =>
    {
        maximum = Math.Max(maximum, Interlocked.Increment(ref active));
        await context.ProgressAsync(50, "half");
        await Task.Delay(40, context.Cancellation);
        Interlocked.Decrement(ref active);
    });
    await using var queue = await JobQueue.CreateAsync(store, runner, new NoExecutor(), Path.Combine(root, "serial"));
    var jobs = new List<Job>();
    for (int i = 0; i < 4; i++) jobs.Add(await queue.SubmitAsync(DiagnosticKind.Storage, Policy(), null));
    await WaitFor(async () => (await queue.ListAsync()).All(j => j.State.IsTerminal()));
    Require(maximum == 1 && store.Events.Count == 16, "single queue / four events each");
    foreach (var group in store.Events.GroupBy(e => e.JobId))
        Require(group.Select(e => e.Sequence).SequenceEqual(new ulong[] { 1, 2, 3, 4 }) && group.All(e => e.Payload.GetProperty("sequence").GetUInt64() == e.Sequence), "snapshot event atomicity");
});
await Check("cancel-late-write-and-queue-slot", async () =>
{
    var store = new MemoryJobStore();
    var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var attempted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    int invocation = 0, writes = 0;
    var runner = new DelegateRunner(async (_, context) =>
    {
        if (Interlocked.Increment(ref invocation) == 1)
        {
            await context.CommitAsync(_ => { writes++; return Task.CompletedTask; });
            started.SetResult(); await release.Task;
            try { await context.CommitAsync(_ => { writes++; return Task.CompletedTask; }); }
            catch (OperationCanceledException) { attempted.SetResult(); }
        }
    });
    await using var queue = await JobQueue.CreateAsync(store, runner, new NoExecutor(), Path.Combine(root, "late"));
    var first = await queue.SubmitAsync(DiagnosticKind.Storage, Policy(), null); await started.Task.WaitAsync(TimeSpan.FromSeconds(5));
    var next = await queue.SubmitAsync(DiagnosticKind.Storage, Policy(), null);
    await queue.CancelAsync(first.Id); await Task.Delay(70);
    Require(invocation == 1 && (await queue.GetAsync(next.Id)).State == JobState.Queued, "slot released before previous worker exit");
    release.SetResult(); await attempted.Task.WaitAsync(TimeSpan.FromSeconds(5));
    await WaitFor(async () => (await queue.GetAsync(next.Id)).State == JobState.Completed);
    Require(writes == 1 && (await queue.GetAsync(first.Id)).State == JobState.Cancelled, "late write or overwritten terminal");
});
await Check("permission-pause-and-denial", async () =>
{
    var store = new MemoryJobStore();
    var executor = new CountingExecutor();
    var runner = new DelegateRunner(async (job, context) => { await context.ExecuteAsync(Call(job.Id)); });
    await using var queue = await JobQueue.CreateAsync(store, runner, executor, Path.Combine(root, "permission"));
    var first = await queue.SubmitAsync(DiagnosticKind.Permission, Policy(ExecutionMode.RequestPermission), null);
    await WaitFor(async () => (await queue.GetAsync(first.Id)).State == JobState.WaitingPermission);
    Require(executor.Calls == 0, "unapproved execution");
    var pending = (await queue.GetAsync(first.Id)).PendingPermission!;
    await queue.DecideAsync(first.Id, pending.Digest, PermissionChoice.Deny);
    await WaitFor(async () => (await queue.GetAsync(first.Id)).State == JobState.Failed);
    Require(executor.Calls == 0, "denied execution");
    var second = await queue.SubmitAsync(DiagnosticKind.Permission, Policy(ExecutionMode.RequestPermission), null);
    await WaitFor(async () => (await queue.GetAsync(second.Id)).State == JobState.WaitingPermission);
    pending = (await queue.GetAsync(second.Id)).PendingPermission!;
    await queue.DecideAsync(second.Id, pending.Digest, PermissionChoice.AllowOnce);
    await WaitFor(async () => (await queue.GetAsync(second.Id)).State == JobState.Completed);
    Require(executor.Calls == 1, "approved call count");
});
await Check("restart-interrupts-and-storage-failure-stops", async () =>
{
    var store = new MemoryJobStore();
    var old = new Job
    {
        Id = "previous",
        Kind = DiagnosticKind.Storage,
        State = JobState.WaitingPermission,
        CreatedAt = 1,
        UpdatedAt = 2,
        Sequence = 2,
        Progress = 50,
        Message = "old",
        ExecutionPolicy = Policy(),
        PendingPermission = new() { Call = Call("previous"), Digest = "stale" }
    };
    store.Jobs[old.Id] = old;
    int runs = 0;
    await using var queue = await JobQueue.CreateAsync(store, new DelegateRunner((_, _) => { runs++; return Task.CompletedTask; }), new NoExecutor(), Path.Combine(root, "restart"));
    Require((await queue.GetAsync("previous")).State == JobState.Interrupted && (await queue.GetAsync("previous")).PendingPermission is null && runs == 0, "restart replay");
    store.FailCommit = true;
    try { await queue.SubmitAsync(DiagnosticKind.Storage, Policy(), null); throw new Exception("failed store accepted"); } catch (IOException) { }
    try { await queue.SubmitAsync(DiagnosticKind.Storage, Policy(), null); throw new Exception("queue did not stop"); } catch (IOException) { }
    Require(runs == 0, "ran after failed persistence");
});

var processRunner = new WindowsProcessRunner();
ProcessRequest Request(string mode, params string[] extra) => new(Environment.ProcessPath!, new[] { "--child", mode }.Concat(extra).ToArray(), root,
    WindowsProcessRunner.CreateEnvironment(AppContext.BaseDirectory, root), TimeSpan.FromSeconds(5));
await Check("process-unicode-argv-environment-and-failure", async () =>
{
    Environment.SetEnvironmentVariable("OMS_SYNTHETIC_SECRET", "must-not-inherit");
    try
    {
        var result = await processRunner.RunAsync(Request("echo", "a b", "引号\"x", "尾\\"));
        Require(result.Succeeded && result.Stdout.Contains("a b|引号\"x|尾\\") && result.Stdout.Contains("secret=absent") && result.Stdout.Contains("合成 任务"), result.Stdout + result.Stderr);
        result = await processRunner.RunAsync(Request("failure"));
        Require(result.Status == "failed" && result.ExitCode == 7 && result.Stderr.Contains("synthetic failure"), "nonzero status");
    }
    finally { Environment.SetEnvironmentVariable("OMS_SYNTHETIC_SECRET", null); }
});
await Check("process-timeout-cancel-output-limit", async () =>
{
    var result = await processRunner.RunAsync(Request("sleep") with { Timeout = TimeSpan.FromMilliseconds(150) });
    Require(result.Status == "timed_out", "timeout");
    using var cts = new CancellationTokenSource(150);
    result = await processRunner.RunAsync(Request("sleep"), cts.Token);
    Require(result.Status == "cancelled", "cancel");
    result = await processRunner.RunAsync(Request("flood") with { OutputLimit = 8192 });
    Require(result.Status == "output_limit" && System.Text.Encoding.UTF8.GetByteCount(result.Stdout + result.Stderr) <= 8192, "bounded output");
});
await Check("normal-root-exit-cleans-descendant", async () =>
{
    var pidFile = Path.Combine(root, "child.pid");
    var result = await processRunner.RunAsync(Request("spawn", pidFile));
    Require(result.Succeeded && File.Exists(pidFile), "did not spawn descendant");
    int pid = int.Parse(await File.ReadAllTextAsync(pidFile));
    bool alive;
    try { using var child = Process.GetProcessById(pid); alive = !child.HasExited; } catch (ArgumentException) { alive = false; }
    Require(!alive, "descendant survived normal root exit");
});

var runtimeArgument = Array.IndexOf(args, "--runtime");
if (runtimeArgument >= 0 && runtimeArgument + 1 < args.Length)
{
    await Check("private-python-computation-and-powershell-arguments", async () =>
    {
        var runtime = Path.GetFullPath(args[runtimeArgument + 1]);
        var executor = new HostToolExecutor(runtime);
        using var broker = new PermissionBroker(); broker.RegisterJob("computation", Policy());
        var call = Call("computation", "python", "import numpy,sympy,matplotlib;matplotlib.use('Agg');import matplotlib.pyplot as p; print(sympy.expand((sympy.Symbol('x')+1)**2));p.plot([0,1],[0,1]);p.savefig('plot.png');print('你好')") with { TimeoutMs = 30000 };
        var result = await executor.ExecuteAsync(broker.Request(call)!, Path.Combine(root, "python-call"), CancellationToken.None);
        Require(result.Succeeded && result.Stdout.Contains("x**2 + 2*x + 1") && result.Stdout.Contains("你好") && File.Exists(Path.Combine(root, "plot.png")), "private runtime computation");
        call = Call("computation", "shell", "param($first,$second,$third); [Console]::WriteLine(($first,$second,$third -join '|'))") with { Tool = ToolKind.Shell, TimeoutMs = 30000 };
        result = await executor.ExecuteAsync(broker.Request(call)!, Path.Combine(root, "shell-call"), CancellationToken.None);
        Require(result.Succeeded && result.Stdout.Contains("a b|尾\\|中文"), result.Stderr + result.Stdout);
    });
    await Check("sandbox-worker-isolated-host-computation", async () =>
    {
        var runtime = Path.GetFullPath(args[runtimeArgument + 1]);
        var result = await processRunner.RunAsync(new(Path.Combine(runtime, "python.exe"),
            ["-I", "-X", "utf8", Path.Combine(AppContext.BaseDirectory, "sandbox_worker_checks.py"), Path.Combine(AppContext.BaseDirectory, "sandbox-worker.py"), runtime],
            root, WindowsProcessRunner.CreateEnvironment(runtime, root), TimeSpan.FromSeconds(45)));
        Require(result.Succeeded && result.Stdout.Contains("powershell-arguments"), result.Stderr + result.Stdout);
    });
}

await SandboxChecks.RunAsync(root, Check, Require);

var report = new CheckReport(DateTimeOffset.UtcNow, RuntimeFeature.IsDynamicCodeSupported, root, results.ToArray(),
    runtimeArgument >= 0 ? ["real-windows-sandbox-session-ownership", "clean-windows-matrix"] : ["real-windows-sandbox-session-ownership", "private-python-packages-when-not-prepared", "clean-windows-matrix"]);
File.WriteAllText(Path.Combine(output, "execution-report.json"), JsonSerializer.Serialize(report, TestJson.Default.CheckReport));
return results.All(r => r.Passed) ? 0 : 1;

internal sealed record CheckResult(string Name, bool Passed, string? Error);
internal sealed record CheckReport(DateTimeOffset TimestampUtc, bool DynamicCodeSupported, string SyntheticRoot, CheckResult[] Results, string[] NotRun);
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(CheckReport))]
internal sealed partial class TestJson : JsonSerializerContext;
internal sealed class MemoryJobStore : IJobStore
{
    public Dictionary<string, Job> Jobs { get; } = new(StringComparer.Ordinal);
    public List<JobEvent> Events { get; } = [];
    public bool FailCommit { get; set; }
    public Task<IReadOnlyList<Job>> LoadJobsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<Job>>(Jobs.Values.ToArray());
    public Task CommitAsync(Job job, JobEvent jobEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailCommit) throw new IOException("synthetic disk failure");
        Jobs[job.Id] = job; Events.Add(jobEvent); return Task.CompletedTask;
    }
}
internal sealed class DelegateRunner(Func<Job, JobContext, Task> execute) : IJobRunner { public Task RunAsync(Job job, JobContext context) => execute(job, context); }
internal sealed class NoExecutor : IToolExecutor
{ public Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall call, string resultsDirectory, CancellationToken cancellationToken) => throw new InvalidOperationException("unexpected execution"); }
internal sealed class CountingExecutor : IToolExecutor
{
    public int Calls;
    public Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall call, string resultsDirectory, CancellationToken cancellationToken)
    { call.Consume(); Interlocked.Increment(ref Calls); return Task.FromResult(new ToolExecutionResult("completed", 0, "ok", "")); }
}
internal static class Child
{
    public static async Task<int> RunAsync(string[] arguments)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        switch (arguments[0])
        {
            case "echo": Console.WriteLine(string.Join('|', arguments[1..])); Console.WriteLine("secret=" + (Environment.GetEnvironmentVariable("OMS_SYNTHETIC_SECRET") is null ? "absent" : "present")); Console.WriteLine(Environment.CurrentDirectory); return 0;
            case "failure": Console.Error.WriteLine("synthetic failure"); return 7;
            case "sleep": await Task.Delay(30000); return 0;
            case "flood": while (true) Console.Write(new string('x', 8192));
            case "spawn":
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--child"); start.ArgumentList.Add("sleep");
                using (var child = Process.Start(start)!) await File.WriteAllTextAsync(arguments[1], child.Id.ToString());
                return 0;
            default: return 2;
        }
    }
}
