using OhMyStudy.Domain;

namespace OhMyStudy.Application.Execution;

public enum PermissionState { WaitingPermission, Allowed, Denied, Cancelled }

/// <summary>An approval for one immutable call. Only the broker can create it.</summary>
public sealed class AuthorizedCall
{
    private readonly CancellationToken _revocation;
    private int _consumed;
    private readonly string _json;

    internal AuthorizedCall(ExecutionCall call, ExecutionMode mode, CancellationToken revocation)
    {
        // Snapshot nested lists too: editing an argument after review must not change execution.
        _json = DomainJson.Serialize(call);
        Mode = mode;
        _revocation = revocation;
    }

    public ExecutionCall Call => DomainJson.Deserialize<ExecutionCall>(_json);
    public ExecutionMode Mode { get; }
    public bool IsLive => !_revocation.IsCancellationRequested;
    public CancellationToken Revocation => _revocation;

    public ExecutionCall Consume()
    {
        _revocation.ThrowIfCancellationRequested();
        if (Interlocked.Exchange(ref _consumed, 1) != 0)
            throw new InvalidOperationException("执行许可已使用。");
        return Call;
    }
}

/// <summary>One pending call pauses its whole job. Grants never survive a restart.</summary>
public sealed class PermissionBroker : IDisposable
{
    private readonly object _sync = new();
    private readonly Dictionary<string, (ExecutionPolicy Policy, CancellationTokenSource Live)> _jobs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ExecutionCall> _waiting = new(StringComparer.Ordinal);
    private readonly HashSet<string> _taskGrants = new(StringComparer.Ordinal);
    private readonly HashSet<(string Job, string Call)> _used = [];
    private readonly Dictionary<string, PermissionState> _stopped = new(StringComparer.Ordinal);

    public void RegisterJob(string id, ExecutionPolicy policy)
    {
        lock (_sync)
        {
            if (!ValidId(id) || _jobs.ContainsKey(id) || _stopped.ContainsKey(id))
                throw new InvalidOperationException("无效或已使用的任务标识。");
            _jobs.Add(id, (policy, new CancellationTokenSource()));
        }
    }

    public AuthorizedCall? Request(ExecutionCall call)
    {
        call.Validate();
        var snapshot = DomainJson.Deserialize<ExecutionCall>(DomainJson.Serialize(call));
        lock (_sync)
        {
            if (!_jobs.TryGetValue(call.JobId, out var job) || _stopped.ContainsKey(call.JobId))
                throw new InvalidOperationException("任务已停止或未注册。");
            if (_waiting.ContainsKey(call.JobId)) throw new InvalidOperationException("任务正在等待执行许可。");
            if (!_used.Add((call.JobId, call.CallId))) throw new InvalidOperationException("调用标识已使用，不能重放。");
            if (job.Policy.EffectiveMode != ExecutionMode.RequestPermission || _taskGrants.Contains(call.JobId))
                return new(snapshot, job.Policy.EffectiveMode, job.Live.Token);
            _waiting.Add(call.JobId, snapshot);
            return null;
        }
    }

    public AuthorizedCall? Decide(string jobId, string digest, PermissionChoice choice)
    {
        lock (_sync)
        {
            if (!_waiting.TryGetValue(jobId, out var call)) throw new InvalidOperationException("执行请求已失效。");
            if (!string.Equals(call.Digest(), digest, StringComparison.Ordinal))
                throw new InvalidOperationException("待执行内容已变化，请重新查看。");
            _waiting.Remove(jobId);
            if (choice == PermissionChoice.Deny)
            {
                CloseCore(jobId, PermissionState.Denied);
                return null;
            }
            if (choice == PermissionChoice.AllowTask) _taskGrants.Add(jobId);
            return new(call, ExecutionMode.RequestPermission, _jobs[jobId].Live.Token);
        }
    }

    public ExecutionCall? Pending(string id)
    {
        lock (_sync) return _waiting.TryGetValue(id, out var call)
            ? DomainJson.Deserialize<ExecutionCall>(DomainJson.Serialize(call)) : null;
    }

    public PermissionState? State(string id)
    {
        lock (_sync) return _stopped.TryGetValue(id, out var state) ? state
            : _waiting.ContainsKey(id) ? PermissionState.WaitingPermission : null;
    }

    public void Close(string id, PermissionState state = PermissionState.Cancelled)
    {
        lock (_sync) CloseCore(id, state);
    }

    private void CloseCore(string id, PermissionState state)
    {
        _waiting.Remove(id);
        _taskGrants.Remove(id);
        if (_jobs.Remove(id, out var job)) { job.Live.Cancel(); job.Live.Dispose(); }
        _stopped[id] = state;
    }

    public void Dispose()
    {
        lock (_sync)
            foreach (var id in _jobs.Keys.ToArray()) CloseCore(id, PermissionState.Cancelled);
    }

    public static bool ValidId(string value) => value.Length is > 0 and <= 128
        && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '_');
}
