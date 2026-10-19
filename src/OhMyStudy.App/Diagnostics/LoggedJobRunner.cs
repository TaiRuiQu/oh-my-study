using System.Diagnostics;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;

namespace OhMyStudy.App.Diagnostics;

internal sealed class LoggedJobRunner(IJobRunner inner) : IJobRunner
{
    public async Task RunAsync(Job job, JobContext context)
    {
        var clock = Stopwatch.StartNew();
        AppLog.Info("Job.Start", $"Job={job.Id}; Kind={job.Kind}");
        try
        {
            await inner.RunAsync(job, context).ConfigureAwait(false);
            AppLog.Info("Job.Completed", $"Job={job.Id}; ElapsedMs={clock.ElapsedMilliseconds}");
        }
        catch (OperationCanceledException) when (context.Cancellation.IsCancellationRequested)
        {
            AppLog.Info("Job.Cancelled", $"Job={job.Id}");
            throw;
        }
        catch (Exception error)
        {
            AppLog.Error($"Job.Failed.{job.Id}", error);
            throw;
        }
    }
}
