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
    catch (Exception error) { results.Add(new(name, false, error.GetType().Name + ": " + error.Message)); Console.Error.WriteLine(error); }
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
    Require(executor.Calls == 1 && executor.Closed.Contains(first.Id) && executor.Closed.Contains(second.Id), "approved call count and cleanup on denial and completion");
});
await Check("retry-starts-a-fresh-task-after-cleanup", async () =>
{
    var store = new MemoryJobStore();
    var executor = new CountingExecutor();
    int runs = 0;
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var runner = new DelegateRunner(async (job, context) =>
    {
        if (Interlocked.Increment(ref runs) == 1)
        {
            await context.ProgressAsync(40, "Partial progress");
            await context.ExecuteAsync(Call(job.Id));
            throw new IOException("Synthetic failure");
        }
        await release.Task.WaitAsync(context.Cancellation);
    });
    string directory = Path.Combine(root, "retry");
    await using var queue = await JobQueue.CreateAsync(store, runner, executor, directory);
    var failed = await queue.SubmitAsync(DiagnosticKind.Storage, Policy(), null);
    await WaitFor(async () => (await queue.GetAsync(failed.Id)).State == JobState.Failed);
    Directory.CreateDirectory(Path.Combine(directory, failed.Id));
    await File.WriteAllTextAsync(Path.Combine(directory, failed.Id, "partial.txt"), "partial");
    var retried = await queue.RetryAsync(failed.Id);
    Require(executor.Closed.Contains(failed.Id) && !Directory.Exists(Path.Combine(directory, failed.Id)), "previous resources or outputs survived retry");
    Require(retried.Id != failed.Id && retried.State == JobState.Queued && retried.Progress == 0 && retried.Sequence == 1 && retried.Error is null && retried.PendingPermission is null, "retry did not start cleanly");
    Require((await queue.ListAsync()).Count == 1 && store.Events.All(e => e.JobId == retried.Id), "old task history survived");
    try { await queue.RetryAsync(retried.Id); throw new Exception("running retry accepted"); } catch (InvalidOperationException) { }
    release.SetResult();
    await WaitFor(async () => (await queue.GetAsync(retried.Id)).State == JobState.Completed);
    try { await queue.RetryAsync(retried.Id); throw new Exception("completed retry accepted"); } catch (InvalidOperationException) { }
    Require(runs == 2, "retry did not execute exactly once");
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
    result = await processRunner.RunAsync(Request("flood-unicode") with { OutputLimit = 8191 });
    Require(result.Status == "output_limit" && System.Text.Encoding.UTF8.GetByteCount(result.Stdout + result.Stderr) <= 8191
        && (result.Stdout.Contains("你") || result.Stderr.Contains("你")), "bounded Unicode and invalid UTF-8 output");
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
await AppContainerChecks.RunAsync(root, Check, Require);
if (runtimeArgument >= 0 && runtimeArgument + 1 < args.Length)
{
    await Check("private-python-computation", async () =>
    {
        var runtime = Path.GetFullPath(args[runtimeArgument + 1]);
        var executor = new HostToolExecutor(runtime);
        using var broker = new PermissionBroker(); broker.RegisterJob("computation", Policy());
        var call = Call("computation", "python", "import numpy,sympy,matplotlib;matplotlib.use('Agg');import matplotlib.pyplot as p; print(sympy.expand((sympy.Symbol('x')+1)**2));p.plot([0,1],[0,1]);p.savefig('plot.png');print('你好')") with { TimeoutMs = 30000 };
        var result = await executor.ExecuteAsync(broker.Request(call)!, Path.Combine(root, "python-call"), CancellationToken.None);
        await executor.CloseJobAsync("computation");
        Require(result.Succeeded && result.Stdout.Contains("x**2 + 2*x + 1") && result.Stdout.Contains("你好") && File.Exists(Path.Combine(root, "plot.png")), "private runtime computation");
    });
    foreach (var mode in new[] { ExecutionMode.BypassPermission, ExecutionMode.RequestPermission, ExecutionMode.Sandbox })
    {
        await Check("python-notebook-session-" + mode, async () =>
        {
            var runtime = Path.GetFullPath(args[runtimeArgument + 1]);
            var jobId = "notebook-" + mode;
            var work = Path.Combine(root, jobId + "-work"); Directory.CreateDirectory(work);
            IToolExecutor executor = new PolicyToolExecutor(new HostToolExecutor(runtime), new SandboxToolExecutor(runtime));
            using var broker = new PermissionBroker();
            var capability = new Capability { State = CapabilityState.Ready, Reason = "synthetic" };
            broker.RegisterJob(jobId, ExecutionPolicy.Freeze(mode, capability, false));
            int ordinal = 0;
            async Task<ToolExecutionResult> Run(string code, ulong timeout = 30000)
            {
                var call = Call(jobId, "cell-" + ++ordinal, code) with { WorkingDirectory = work, TimeoutMs = timeout };
                var authorization = broker.Request(call);
                if (mode == ExecutionMode.RequestPermission)
                {
                    Require(authorization is null, "each notebook cell still requires approval");
                    authorization = broker.Decide(jobId, call.Digest(), PermissionChoice.AllowOnce);
                }
                return await executor.ExecuteAsync(authorization!, Path.Combine(root, jobId, call.CallId), CancellationToken.None);
            }
            try
            {
                var result = await Run("from PIL import Image;import numpy as np;crop=Image.new('RGB',(8,6),'red');values=np.array([2,3]);def_source='def twice(x): return x*2';exec(def_source);print('created')");
                Require(result.Succeeded, result.Stderr);
                result = await Run("assert crop.size==(8,6);assert twice(values).tolist()==[4,6];crop.save('crop.png');held=open('crop.png','rb');print('shared')");
                Require(result.Succeeded && result.Stdout.Contains("shared") && !result.Stdout.Contains("created") && File.Exists(Path.Combine(work, "crop.png")), "Pillow image, imports, functions and arrays shared with separate output");
                result = await Run("before_error=42;raise ValueError('synthetic notebook error')");
                Require(result.Status == "failed" && result.Stderr.Contains("synthetic notebook error"), "ordinary exception reported");
                result = await Run("assert before_error==42;assert crop.size==(8,6);assert held.read(8)==b'\\x89PNG\\r\\n\\x1a\\n';print('preserved')");
                Require(result.Succeeded && result.Stdout.Contains("preserved"), "exception preserves completed assignments");
                result = await Run("import time;time.sleep(10)", 150);
                Require(result.Status == "timed_out" && result.Stderr.Contains("session reset"), "timeout resets kernel");
                result = await Run("assert 'crop' not in globals();print('reset')");
                Require(result.Succeeded && result.Stdout.Contains("reset"), "new namespace after timeout");
                result = await Run("import os;os._exit(7)");
                Require(result.Status == "failed" && result.Stderr.Contains("session reset"), "worker exit resets kernel without replaying code");
                result = await Run("import os;os.write(1,b'\\xff'*1048577)");
                Require(result.Status == "output_limit" && System.Text.Encoding.UTF8.GetByteCount(result.Stdout + result.Stderr) <= 1_048_576, "native output and malformed UTF-8 stay bounded and reset kernel");
                result = await Run("assert 'os' not in globals();values=9;print('recovered')");
                Require(result.Succeeded && result.Stdout.Contains("recovered"), "fresh namespace after output limit");
                var otherId = jobId + "-other";
                broker.RegisterJob(otherId, ExecutionPolicy.Freeze(mode, capability, false));
                var other = Call(otherId, "isolation", "assert 'values' not in globals();values=100;print('isolated')") with { WorkingDirectory = work, TimeoutMs = 30000 };
                var otherAuthorization = broker.Request(other) ?? broker.Decide(otherId, other.Digest(), PermissionChoice.AllowOnce);
                try
                {
                    result = await executor.ExecuteAsync(otherAuthorization!, Path.Combine(root, otherId, other.CallId), CancellationToken.None);
                    Require(result.Succeeded && result.Stdout.Contains("isolated"), "separate tasks have separate namespaces");
                }
                finally { await executor.CloseJobAsync(otherId); }
                result = await Run("assert values==9");
                Require(result.Succeeded, "another task does not mutate the first namespace");
                await executor.CloseJobAsync(jobId);
                result = await Run("assert 'values' not in globals();print('closed')");
                Require(result.Succeeded && result.Stdout.Contains("closed"), "task cleanup discards namespace");
            }
            finally { await executor.CloseJobAsync(jobId); }
        });
    }
    foreach (var item in new (string Id, string Code, List<string> Arguments, string Output, uint Exit)[]
    {
        ("arguments", "param($a,$b,$c);[Console]::WriteLine(($a,$b,$c -join '|'))", ["a b", "尾\\", "中文"], "a b|尾\\|中文", 0),
        ("literal", "param($a,$b,$c);if($a -ne ''){throw 'empty argument changed'};[Console]::WriteLine($b);[Console]::WriteLine($c)" + new string(' ', 40000), ["", "a'\";$(throw 'must-not-run')", "line1\nline2"], "a'\";$(throw 'must-not-run')", 0),
        ("no-arguments", "[Console]::WriteLine($args.Count)", [], "0", 0),
        ("working-directory", "if(-not (Test-Path -LiteralPath 'plot.png')){throw 'missing Python plot'};Copy-Item -LiteralPath 'plot.png' -Destination 'shell-plot.png';[Console]::WriteLine((Get-Location).Path)", [], root, 0),
        ("nonzero", "exit 7", [], "", 7),
        ("error", "throw 'synthetic error'", [], "", 1)
    })
    {
        await Check("host-powershell-" + item.Id, async () =>
        {
            var runtime = Path.GetFullPath(args[runtimeArgument + 1]);
            using var broker = new PermissionBroker(); broker.RegisterJob(item.Id, Policy());
            var call = Call(item.Id, "shell", item.Code) with { Tool = ToolKind.Shell, TimeoutMs = 120000, Arguments = item.Arguments };
            var result = await new HostToolExecutor(runtime).ExecuteAsync(broker.Request(call)!, Path.Combine(root, "host-shell-" + item.Id), CancellationToken.None);
            Require(result.ExitCode == item.Exit && result.Status == (item.Exit == 0 ? "completed" : "failed") && result.Stdout.Contains(item.Output),
                $"host PowerShell {item.Id}: {result.Status}; exit={result.ExitCode}; {result.Stderr}{result.Stdout}");
        });
    }
    await Check("sandbox-worker-isolated-host-computation", async () =>
    {
        var runtime = Path.GetFullPath(args[runtimeArgument + 1]);
        var result = await processRunner.RunAsync(new(Path.Combine(runtime, "python.exe"),
            ["-I", "-X", "utf8", Path.Combine(AppContext.BaseDirectory, "sandbox_worker_checks.py"), Path.Combine(AppContext.BaseDirectory, "sandbox-worker.py"), runtime],
            root, WindowsProcessRunner.CreateEnvironment(runtime, root), TimeSpan.FromSeconds(120)));
        Require(result.Succeeded && result.Stdout.Contains("powershell-arguments"), result.Stderr + result.Stdout);
    });
    await AppContainerChecks.RunWorkerAsync(Path.GetFullPath(args[runtimeArgument + 1]), root, Check, Require);
}

await SandboxChecks.RunAsync(root, Check, Require);

var report = new CheckReport(DateTimeOffset.UtcNow, RuntimeFeature.IsDynamicCodeSupported, root, results.ToArray(),
    runtimeArgument >= 0 ? ["clean-windows-matrix"] : ["private-python-packages-when-not-prepared", "clean-windows-matrix"]);
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
    public Task RetryAsync(Job previous, Job replacement, JobEvent jobEvent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (FailCommit) throw new IOException("Synthetic retry failure");
        Jobs.Remove(previous.Id); Events.RemoveAll(e => e.JobId == previous.Id);
        return CommitAsync(replacement, jobEvent, cancellationToken);
    }
}
internal sealed class DelegateRunner(Func<Job, JobContext, Task> execute) : IJobRunner { public Task RunAsync(Job job, JobContext context) => execute(job, context); }
internal sealed class NoExecutor : IToolExecutor
{ public Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall call, string resultsDirectory, CancellationToken cancellationToken) => throw new InvalidOperationException("unexpected execution"); }
internal sealed class CountingExecutor : IToolExecutor
{
    public int Calls;
    public HashSet<string> Closed { get; } = new(StringComparer.Ordinal);
    public Task CloseJobAsync(string jobId) { Closed.Add(jobId); return Task.CompletedTask; }
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
            case "appcontainer": return AppContainerChecks.Probe(arguments[1..]);
            case "appcontainer-descendant": return await AppContainerChecks.ProbeDescendantAsync(arguments[1..]);
            case "echo": Console.WriteLine(string.Join('|', arguments[1..])); Console.WriteLine("secret=" + (Environment.GetEnvironmentVariable("OMS_SYNTHETIC_SECRET") is null ? "absent" : "present")); Console.WriteLine(Environment.CurrentDirectory); return 0;
            case "failure": Console.Error.WriteLine("synthetic failure"); return 7;
            case "sleep": await Task.Delay(30000); return 0;
            case "flood": while (true) Console.Write(new string('x', 8192));
            case "flood-unicode":
                using (var output = Console.OpenStandardOutput())
                using (var error = Console.OpenStandardError())
                {
                    byte[] bytes = [0xe4, 0xbd, 0xa0, 0xf0, 0x9f, 0x99, 0x82, 0xff];
                    while (true) { output.Write(bytes); error.Write(bytes); }
                }
            case "spawn":
                var start = new ProcessStartInfo(Environment.ProcessPath!) { UseShellExecute = false, CreateNoWindow = true };
                start.ArgumentList.Add("--child"); start.ArgumentList.Add("sleep");
                using (var child = Process.Start(start)!) await File.WriteAllTextAsync(arguments[1], child.Id.ToString());
                return 0;
            default: return 2;
        }
    }
}
