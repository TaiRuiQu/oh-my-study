using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;
using OhMyStudy.Infrastructure.Execution;

namespace OhMyStudy.Infrastructure.Jobs;

public sealed class LibraryJobStore(LibraryRepository library) : IJobStore
{
    public async Task<IReadOnlyList<Job>> LoadJobsAsync(CancellationToken cancellationToken = default)
        => await library.LoadJobSnapshotsAsync(cancellationToken).ConfigureAwait(false);
    public Task CommitAsync(Job job, JobEvent jobEvent, CancellationToken cancellationToken = default)
        => library.SaveJobEventAsync(job, jobEvent, cancellationToken);

    public async Task RetryAsync(Job previous, Job replacement, JobEvent jobEvent, CancellationToken cancellationToken = default)
    {
        if (!Guid.TryParse(previous.Id, out _)) throw new ArgumentException("Invalid task ID.");
        string root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "OhMyStudy", "tasks"));
        string directory = Path.GetFullPath(Path.Combine(root, previous.Id));
        if (!directory.StartsWith(Path.TrimEndingDirectorySeparator(root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("The task workspace is outside the tasks directory.");
        if (Directory.Exists(directory)) Directory.Delete(RuntimePaths.CheckedDirectory(directory), recursive: true);
        await library.RetryJobAsync(previous, replacement, jobEvent, cancellationToken).ConfigureAwait(false);
    }
}
