using OhMyStudy.Application.Execution;
using OhMyStudy.Domain;

namespace OhMyStudy.Application.Jobs;

public interface IJobStore
{
    Task<IReadOnlyList<Job>> LoadJobsAsync(CancellationToken cancellationToken = default);
    Task CommitAsync(Job job, JobEvent jobEvent, CancellationToken cancellationToken = default);
}

public interface IJobRunner
{
    Task RunAsync(Job job, JobContext context);
}

public interface IToolExecutor
{
    Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall call, string resultsDirectory, CancellationToken cancellationToken);
}

public sealed record ToolExecutionResult(string Status, uint? ExitCode, string Stdout, string Stderr)
{
    public bool Succeeded => Status == "completed";
}

/// <summary>Only the queue constructs a context; every result write crosses its cancellation barrier.</summary>
public sealed class JobContext
{
    private readonly JobQueue _owner;
    internal JobContext(JobQueue owner, string id, CancellationToken cancellation)
    { _owner = owner; JobId = id; Cancellation = cancellation; }
    public string JobId { get; }
    public CancellationToken Cancellation { get; }
    public Task ProgressAsync(double progress, string message) => _owner.ProgressAsync(JobId, progress, message);
    public Task<T> CommitAsync<T>(Func<CancellationToken, Task<T>> operation) => _owner.CommitResultAsync(JobId, operation);
    public Task CommitAsync(Func<CancellationToken, Task> operation) => _owner.CommitResultAsync(JobId,
        async token => { await operation(token).ConfigureAwait(false); return true; });
    public Task<ToolExecutionResult> ExecuteAsync(ExecutionCall call) => _owner.ExecuteToolAsync(JobId, call);
}
