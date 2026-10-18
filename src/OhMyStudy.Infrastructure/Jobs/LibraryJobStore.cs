using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Jobs;

public sealed class LibraryJobStore(LibraryRepository library) : IJobStore
{
    public async Task<IReadOnlyList<Job>> LoadJobsAsync(CancellationToken cancellationToken = default)
        => await library.LoadJobSnapshotsAsync(cancellationToken).ConfigureAwait(false);
    public Task CommitAsync(Job job, JobEvent jobEvent, CancellationToken cancellationToken = default)
        => library.SaveJobEventAsync(job, jobEvent, cancellationToken);
}
