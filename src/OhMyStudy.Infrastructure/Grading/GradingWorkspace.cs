using OhMyStudy.Infrastructure.Execution;

namespace OhMyStudy.Infrastructure.Grading;

internal sealed class GradingWorkspace(string jobId)
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "OhMyStudy", "tasks", jobId);
    public void Prepare() => RuntimePaths.CheckedDirectory(DirectoryPath, create: true);
}
