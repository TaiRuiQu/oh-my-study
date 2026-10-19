using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using OhMyStudy.Application.Execution;
using OhMyStudy.Domain;

namespace OhMyStudy.Application.Jobs;

/// <summary>A single reader owns the execution slot until the runner and all owned processes exit.</summary>
public sealed class JobQueue : IAsyncDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly IJobStore _store;
    private readonly IJobRunner _runner;
    private readonly IToolExecutor _executor;
    private readonly string _resultsRoot;
    private readonly PermissionBroker _broker = new();
    private readonly Dictionary<string, Job> _jobs = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, Control> _controls = new(StringComparer.Ordinal);
    private readonly Channel<string> _pending = Channel.CreateBounded<string>(new BoundedChannelOptions(128)
    { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ConcurrentDictionary<long, Channel<JobEvent>> _subscribers = new();
    private long _subscriberId;
    private Task _worker = Task.CompletedTask;
    private volatile bool _stopping;
    private Exception? _fatal;

    private sealed class Control : IDisposable
    {
        private readonly object _sync = new();
        private bool _disposed;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<AuthorizedCall?>? Permission { get; set; }
        public void Cancel() { lock (_sync) { if (!_disposed) Cancellation.Cancel(); } }
        public void Dispose() { lock (_sync) { if (_disposed) return; _disposed = true; Cancellation.Dispose(); } }
    }

    private JobQueue(IJobStore store, IJobRunner runner, IToolExecutor executor, string resultsRoot)
    { _store = store; _runner = runner; _executor = executor; _resultsRoot = Path.GetFullPath(resultsRoot); }

    public static async Task<JobQueue> CreateAsync(IJobStore store, IJobRunner runner, IToolExecutor executor,
        string resultsRoot, CancellationToken cancellationToken = default)
    {
        if (!Path.IsPathFullyQualified(resultsRoot)) throw new ArgumentException("任务目录必须是绝对路径。", nameof(resultsRoot));
        Directory.CreateDirectory(resultsRoot);
        var queue = new JobQueue(store, runner, executor, resultsRoot);
        foreach (var old in await store.LoadJobsAsync(cancellationToken).ConfigureAwait(false))
        {
            if (queue._jobs.ContainsKey(old.Id)) throw new InvalidDataException("重复的任务标识。");
            var job = Snapshot(old);
            if (!job.State.IsTerminal())
            {
                job = Bump(job with
                {
                    State = JobState.Interrupted,
                    PendingPermission = null,
                    Message = "任务因应用重启而中断。",
                    Error = "上次任务未完成，未自动重新执行。"
                });
                await store.CommitAsync(job, Event(job, "interrupted"), cancellationToken).ConfigureAwait(false);
            }
            queue._jobs.Add(job.Id, job);
        }
        queue._worker = Task.Run(queue.RunAsync, CancellationToken.None);
        return queue;
    }

    public async Task<Job> SubmitAsync(DiagnosticKind kind, ExecutionPolicy policy, GradingInput? input,
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Healthy();
            if (_jobs.Values.Count(j => !j.State.IsTerminal()) >= 128) throw new InvalidOperationException("等待的任务过多，请稍后再试。");
            if (input is not null && _jobs.Values.Any(j => !j.State.IsTerminal() && j.Input?.Document.Id == input.Document.Id))
                throw new InvalidOperationException("这份资料已有未完成的任务。");
            if (kind is DiagnosticKind.Grading or DiagnosticKind.References && input is null)
                throw new ArgumentException("整卷任务缺少资料。");
            ulong now = Now();
            var job = new Job
            {
                Id = Guid.NewGuid().ToString(),
                Kind = kind,
                State = JobState.Queued,
                CreatedAt = now,
                UpdatedAt = now,
                Sequence = 1,
                Progress = 0,
                Message = "已加入队列。",
                ExecutionPolicy = policy,
                PendingPermission = null,
                Error = null,
                Input = input
            };
            job = Snapshot(job);
            // Persist before exposing the job or making execution possible.
            await PersistAsync(job, "queued", cancellationToken).ConfigureAwait(false);
            _broker.RegisterJob(job.Id, policy);
            _controls[job.Id] = new();
            if (!_pending.Writer.TryWrite(job.Id)) throw new InvalidOperationException("任务队列已停止。");
            return Snapshot(job);
        }
        finally { _gate.Release(); }
    }

    public async Task<IReadOnlyList<Job>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowFatal(); return _jobs.Values.OrderByDescending(j => j.CreatedAt).ThenByDescending(j => j.Id, StringComparer.Ordinal).Select(Snapshot).ToArray(); }
        finally { _gate.Release(); }
    }

    public async Task<Job> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ThrowFatal(); return Snapshot(Get(id)); }
        finally { _gate.Release(); }
    }

    /// <summary>Bounded streams carry full snapshots; on sequence gaps, refresh ListAsync.</summary>
    public async IAsyncEnumerable<JobEvent> WatchAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var channel = Channel.CreateBounded<JobEvent>(new BoundedChannelOptions(256) { SingleReader = true, FullMode = BoundedChannelFullMode.DropOldest });
        var id = Interlocked.Increment(ref _subscriberId);
        _subscribers[id] = channel;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false)) yield return item;
        }
        finally { _subscribers.TryRemove(id, out _); }
    }

    public async Task<Job> CancelAsync(string id, CancellationToken cancellationToken = default)
    {
        // Revoke even if storage subsequently fails. A cancelled task cannot receive a new grant.
        if (_controls.TryGetValue(id, out var control)) control.Cancel();
        _broker.Close(id);
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var job = Get(id);
            if (job.State.IsTerminal()) return Snapshot(job);
            bool queued = job.State == JobState.Queued;
            control?.Permission?.TrySetCanceled();
            job = Bump(job with { State = JobState.Cancelled, PendingPermission = null, Message = "任务已取消。" });
            await PersistAsync(job, "cancelled", CancellationToken.None).ConfigureAwait(false);
            if (queued && _controls.TryRemove(id, out var removed)) removed.Dispose();
            return Snapshot(job);
        }
        finally { _gate.Release(); }
    }

    public async Task<Job> DecideAsync(string id, string digest, PermissionChoice choice, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Healthy();
            var job = Get(id);
            if (job.State != JobState.WaitingPermission || !_controls.TryGetValue(id, out var control) || control.Permission is null)
                throw new InvalidOperationException("执行请求已失效。");
            control.Cancellation.Token.ThrowIfCancellationRequested();
            var approved = _broker.Decide(id, digest, choice);
            var denied = choice == PermissionChoice.Deny;
            job = Bump(job with
            {
                State = denied ? JobState.Failed : JobState.Running,
                PendingPermission = null,
                Message = denied ? "已拒绝执行。" : "继续处理。",
                Error = denied ? "用户拒绝执行。" : null
            });
            await PersistAsync(job, denied ? "permission_denied" : "permission_allowed", CancellationToken.None).ConfigureAwait(false);
            if (denied) control.Cancel();
            control.Permission.TrySetResult(approved);
            return Snapshot(job);
        }
        finally { _gate.Release(); }
    }

    internal async Task ProgressAsync(string id, double progress, string message)
    {
        if (!double.IsFinite(progress) || progress is < 0 or > 100) throw new ArgumentOutOfRangeException(nameof(progress));
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLive(id);
            var job = Bump(Get(id) with { State = JobState.Running, Progress = progress, Message = message });
            await PersistAsync(job, "progress", CancellationToken.None).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    internal async Task<T> CommitResultAsync<T>(string id, Func<CancellationToken, Task<T>> operation)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { var control = EnsureLive(id); return await operation(control.Cancellation.Token).ConfigureAwait(false); }
        finally { _gate.Release(); }
    }

    internal async Task<ToolExecutionResult> ExecuteToolAsync(string id, ExecutionCall call)
    {
        AuthorizedCall? approved;
        Task<AuthorizedCall?>? permission = null;
        Control control;
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            control = EnsureLive(id);
            if (call.JobId != id) throw new InvalidOperationException("调用不属于当前任务。");
            approved = _broker.Request(call);
            if (approved is null)
            {
                control.Permission = new(TaskCreationOptions.RunContinuationsAsynchronously);
                permission = control.Permission.Task;
                var job = Bump(Get(id) with
                {
                    State = JobState.WaitingPermission,
                    Message = "等待执行许可。",
                    PendingPermission = new PermissionReview { Call = call, Digest = call.Digest() }
                });
                await PersistAsync(job, "permission_requested", CancellationToken.None).ConfigureAwait(false);
            }
        }
        finally { _gate.Release(); }
        if (permission is not null) approved = await permission.WaitAsync(control.Cancellation.Token).ConfigureAwait(false);
        control.Cancellation.Token.ThrowIfCancellationRequested();
        if (approved is null) throw new OperationCanceledException("执行未获允许。", control.Cancellation.Token);
        // The executor enforces the frozen environment and never replays a failed sandbox call on host.
        return await _executor.ExecuteAsync(approved, Path.Combine(_resultsRoot, id, call.CallId), control.Cancellation.Token).ConfigureAwait(false);
    }

    private async Task RunAsync()
    {
        await foreach (var id in _pending.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            if (_stopping) break;
            Job job;
            Control control;
            await _gate.WaitAsync().ConfigureAwait(false);
            try
            {
                if (Get(id).State != JobState.Queued || !_controls.TryGetValue(id, out control!)) continue;
                if (control.Cancellation.IsCancellationRequested) continue;
                job = Bump(Get(id) with { State = JobState.Running, Message = "正在处理。" });
                await PersistAsync(job, "started", CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception error) { StopOnFailure(error); break; }
            finally { _gate.Release(); }
            try
            {
                await _runner.RunAsync(Snapshot(job), new(this, id, control.Cancellation.Token)).ConfigureAwait(false);
                control.Cancellation.Token.ThrowIfCancellationRequested();
                await CompleteAsync(id, null).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (control.Cancellation.IsCancellationRequested) { await CompleteAsync(id, "cancelled").ConfigureAwait(false); }
            catch (Exception) { await CompleteAsync(id, "failed").ConfigureAwait(false); }
            finally
            {
                _broker.Close(id);
                if (_controls.TryRemove(id, out var removed)) removed.Dispose();
            }
        }
    }

    private async Task CompleteAsync(string id, string? failure)
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            var job = Get(id);
            if (job.State.IsTerminal() || _fatal is not null) return;
            var state = failure switch { "cancelled" => JobState.Cancelled, "failed" => JobState.Failed, _ => JobState.Completed };
            job = Bump(job with
            {
                State = state,
                Progress = failure is null ? 100 : job.Progress,
                PendingPermission = null,
                Message = failure is null ? "已完成。" : failure == "cancelled" ? "已取消。" : "处理未完成，可检查设置后重试。",
                Error = failure == "failed" ? "任务失败；已保存的结果会保留。" : null
            });
            await PersistAsync(job, failure ?? "completed", CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception error) { StopOnFailure(error); }
        finally { _gate.Release(); }
    }

    internal async Task ActivityAsync(string id, JobActivity activity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(activity.Key);
        if (activity.Key.Length > 128 || activity.Title.Length > 200 || activity.Detail.Length > 1000 || activity.Text.Length > 16000)
            throw new ArgumentException("任务步骤超过长度限制。", nameof(activity));
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            EnsureLive(id);
            await PersistAsync(Bump(Get(id)), "activity", CancellationToken.None, DomainJson.ToElement(activity)).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    private async Task PersistAsync(Job job, string kind, CancellationToken cancellationToken, System.Text.Json.JsonElement? payload = null)
    {
        var item = Event(job, kind);
        if (payload is { } value) item = item with { Payload = value };
        try { await _store.CommitAsync(job, item, cancellationToken).ConfigureAwait(false); }
        catch (Exception error) { StopOnFailure(error); throw new IOException("任务记录未能保存，队列已停止。", error); }
        _jobs[job.Id] = job;
        foreach (var subscriber in _subscribers.Values) subscriber.Writer.TryWrite(item);
    }

    private void StopOnFailure(Exception error)
    {
        _fatal ??= error; _stopping = true; _pending.Writer.TryComplete();
        foreach (var (id, control) in _controls) { _broker.Close(id); control.Cancel(); control.Permission?.TrySetCanceled(); }
    }
    private void Healthy() { ThrowFatal(); if (_stopping) throw new InvalidOperationException("应用正在退出。"); }
    private void ThrowFatal() { if (_fatal is not null) throw new IOException("任务队列已停止，请重新打开应用。", _fatal); }
    private Control EnsureLive(string id)
    {
        Healthy();
        if (Get(id).State.IsTerminal() || !_controls.TryGetValue(id, out var control)) throw new OperationCanceledException("任务已停止。");
        control.Cancellation.Token.ThrowIfCancellationRequested();
        return control;
    }
    private Job Get(string id) => _jobs.TryGetValue(id, out var job) ? job : throw new KeyNotFoundException("找不到任务。");
    private static Job Snapshot(Job job) => DomainJson.Deserialize<Job>(DomainJson.Serialize(job));
    private static ulong Now() => checked((ulong)DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
    private static Job Bump(Job job) => job with { Sequence = checked(job.Sequence + 1), UpdatedAt = Now() };
    private static JobEvent Event(Job job, string kind) => new()
    {
        Id = $"{job.Id}-{job.Sequence}",
        JobId = job.Id,
        Sequence = job.Sequence,
        CreatedAt = job.UpdatedAt,
        Kind = kind,
        Payload = DomainJson.ToElement(job)
    };

    public async ValueTask DisposeAsync()
    {
        string[] ids;
        await _gate.WaitAsync().ConfigureAwait(false);
        try { _stopping = true; _pending.Writer.TryComplete(); ids = _controls.Keys.ToArray(); }
        finally { _gate.Release(); }
        foreach (var id in ids) await CancelAsync(id).ConfigureAwait(false);
        await _worker.ConfigureAwait(false);
        foreach (var (_, control) in _controls) control.Dispose();
        _controls.Clear(); _broker.Dispose();
        foreach (var stream in _subscribers.Values) stream.Writer.TryComplete();
    }
}
