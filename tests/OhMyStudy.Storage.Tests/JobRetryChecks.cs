using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

internal static partial class Program
{
    private static async Task JobRetryChecks()
    {
        string path = CreateLegacy(2);
        Job failed;
        Job replacement;
        SavedQuestion original;
        string generatedFile;
        await using (var library = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite) { MediaProcessor = new FixtureProcessor() }))
        {
            var baseline = Fixture<Job>("job") with { State = JobState.Running, Sequence = 1, Progress = 0 };
            await library.SaveJobEventAsync(baseline, Event(baseline));
            await library.BeginGradingAsync(baseline);
            await library.SaveGradingReferencesAsync(baseline.Id, Fixture<List<ReferenceAnswer>>("references"));
            original = await library.SaveGradingQuestionAsync(baseline.Id, Fixture<Question>("question"));
            await library.SaveGradingPaperInfoAsync(baseline.Id, "Original subject", 1, 2);
            var completed = baseline with { State = JobState.Completed, Sequence = 2 };
            await library.SaveJobEventAsync(completed, Event(completed));
            var running = baseline with { Id = Guid.NewGuid().ToString(), CreatedAt = baseline.CreatedAt + 1 };
            await library.SaveJobEventAsync(running, Event(running));
            await library.BeginGradingAsync(running);
            var changed = original.Question with { Number = "Changed" };
            await library.SaveGradingQuestionAsync(running.Id, changed);
            await library.SaveGradingQuestionAsync(running.Id, changed with { Number = "Changed twice" });
            var extra = changed with
            {
                DraftKey = "retry-extra",
                Number = "2",
                Items = changed.Items.Select(i => i with
                {
                    Key = "retry-" + i.Key,
                    Attempt = i.Attempt is { } a ? a with { ReferenceKey = null, Grade = a.Grade with { Source = GradeSource.AiSolution } } : null
                }).ToList()
            };
            await library.SaveGradingQuestionAsync(running.Id, extra);
            await library.SaveGradingPaperInfoAsync(running.Id, "Failed subject", 0, 10, name: "Failed title");
            await library.SaveGradingPaperInfoAsync(running.Id, "Failed twice", 0, 20, name: "Failed title twice");
            await library.SaveGradingReferencesAsync(running.Id, Fixture<List<ReferenceAnswer>>("references").Select(r => r with { Answer = "Failed answer" }).ToList());
            await library.RecordModelCallAsync(running.Id, 1, contracts.RootElement.GetProperty("model_call"));
            var asset = await library.SaveGradingImageAsync(running.Id, png);
            generatedFile = Path.Combine(path, "assets", asset.Id + ".png");
            Check(File.Exists(generatedFile), "retry fixture generated image exists");
            failed = running with { State = JobState.Failed, Sequence = 2, Error = "Synthetic failure", Progress = 50 };
            await library.SaveJobEventAsync(failed, Event(failed));
            replacement = failed with { Id = Guid.NewGuid().ToString(), State = JobState.Queued, Sequence = 1, Progress = 0, Error = null, CreatedAt = failed.CreatedAt + 1 };
            await library.SaveQuestionNotesAsync(original.Id, "Manual notes after failure");
            await Fails(() => library.RetryJobAsync(failed, replacement, Event(replacement) with { JobId = failed.Id }), "invalid replacement rolls back cleanup atomically");
            Check((await library.GradingQuestionsAsync(failed.Input!.Document.Id)).Count == 2 && File.Exists(generatedFile), "failed retry retains questions and generated files");
        }
        await using (var library = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite)))
        {
            await library.RetryJobAsync(failed, replacement, Event(replacement));
            var restored = (await library.GradingQuestionsAsync(failed.Input!.Document.Id)).Single();
            Check(restored.Id == original.Id && restored.Question.Number == original.Question.Number && restored.Question.Notes == "Manual notes after failure", "retry after reopening restores original question and preserves notes");
            Check((await library.ListPapersAsync()).Single().Subject == "Original subject", "retry restores paper metadata before first write");
            Check((await library.GetDocumentAsync(failed.Input.Document.Id)).Name == failed.Input.Document.Name, "retry restores the title before the first model rename");
            Check((await library.GradingReferencesAsync(failed.Input.Document.Id)).All(r => r.Answer != "Failed answer"), "retry removes failed reference answers");
            Check(!File.Exists(generatedFile), "retry removes generated asset file");
            Check((await library.ListJobEventsAsync(failed.Id)).Count == 0 && (await library.ListJobEventsAsync(replacement.Id)).Count == 1, "retry replaces failed history with fresh queued event");
            var result = await library.GradingResultAsync(failed.Input.Document.Id);
            Check(result.ModelCalls.Count == 0 && result.Coverage.Count == 0, "retry removes old usage and coverage");
            await Fails(() => library.RetryJobAsync(failed, replacement, Event(replacement)), "same failure cannot be retried twice");
            var restarted = replacement with { State = JobState.Running, Sequence = 2 };
            await library.SaveJobEventAsync(restarted, Event(restarted));
            await library.BeginGradingAsync(restarted);
            await library.SaveGradingQuestionAsync(restarted.Id, original.Question);
            var interrupted = restarted with { State = JobState.Interrupted, Sequence = 3 };
            await library.SaveJobEventAsync(interrupted, Event(interrupted));
            var edited = await library.EditQuestionAsync(original.Id, original.Question with { Number = "Manual edit", Notes = "Manual notes after failure" });
            var next = replacement with { Id = Guid.NewGuid().ToString() };
            await Fails(() => library.RetryJobAsync(interrupted, next, Event(next)), "retry protects manual question edits");
            Check((await library.GradingQuestionsAsync(failed.Input.Document.Id)).Single().Question.Number == edited.Question.Number, "blocked retry preserves edited question");
            await library.VerifyIntegrityAsync();
        }
    }
}
