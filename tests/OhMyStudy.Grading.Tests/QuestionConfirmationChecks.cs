using OhMyStudy.Domain;

internal static partial class Program
{
    private static async Task QuestionConfirmationChecks()
    {
        foreach (bool uncertainGrade in new[] { false, true })
        {
            await using var fixture = await Fixture.Create(ImportMode.AiGrading, Protocol.Responses);
            var question = Question(fixture.Document, reference: false) with { Uncertainties = ["核对原图中的数值。"] };
            if (uncertainGrade) question = WithAttempt(question, AttemptOf(question) with { Grade = AttemptOf(question).Grade with { Verdict = Verdict.Uncertain, Earned = null } });
            var api = new ScriptApi(Protocol.Responses, (_, _, _) => Task.FromResult<List<ToolCall>>([
                .. fixture.Document.Pages.Select(page => Read(page)), Save(question), Finish(Coverage(fixture.Document, question.DraftKey))]));
            Check((await fixture.Run(api)).State == JobState.Completed, "question with a transcription concern saves for user review");
            var saved = (await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single();
            await fixture.Library.SaveQuestionNotesAsync(saved.Id, "Keep my notes");
            saved = await fixture.Library.GetQuestionAsync(saved.Id);
            var confirmed = await fixture.Library.ConfirmQuestionAsync(saved.Id);
            Check(confirmed.Id == saved.Id && DomainJson.Serialize(confirmed.Question) == DomainJson.Serialize(saved.Question with { Uncertainties = [] }), "one-click confirmation clears review text and preserves question content, scores, evidence, and notes");
            Check(confirmed.Question.NeedsReview == uncertainGrade, "confirmation does not invent a verdict for an uncertain grade");
            var pending = await fixture.Library.QueryQuestionsAsync(new(OhMyStudy.Infrastructure.Storage.QuestionFilter.NeedsReview));
            Check(pending.Matching == (uncertainGrade ? 1 : 0), "confirmation updates the persisted needs-review filter");
        }
    }
}
