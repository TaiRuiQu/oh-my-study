using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

internal static partial class Program
{
    private static async Task FilesLifecycleChecks()
    {
        foreach (bool cleanupFails in new[] { false, true })
        {
            await using var fixture = await Fixture.Create(ImportMode.Blank, Protocol.Responses);
            var question = Question(fixture.Document, reference: false);
            var client = new DelegateClient(Protocol.Responses, (_, _, _) => Task.FromResult(NativeTurn(Protocol.Responses,
                [.. fixture.Document.Pages.Select(page => Read(page)), Save(question), Finish(Coverage(fixture.Document, question.DraftKey))])))
            {
                Cleanup = _ => cleanupFails ? Task.FromException(new IOException("Synthetic remote cleanup failure")) : Task.CompletedTask
            };
            var job = await fixture.Run(client);
            Check(job.State == JobState.Completed && client.CleanupAttempts == 1 && !client.CleanupTokenCancelled,
                cleanupFails ? "file cleanup failure does not discard completed grading" : "completed grading deletes its uploaded files");
        }
    }
}
