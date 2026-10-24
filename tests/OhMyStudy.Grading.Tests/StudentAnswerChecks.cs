using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

internal static partial class Program
{
    private static async Task PaperNamesAndStudentAnswers()
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            await using var fixture = await Fixture.Create(ImportMode.Blank, protocol);
            var question = Question(fixture.Document, reference: false);
            var api = new ScriptApi(protocol, (_, request, _) =>
            {
                string instructions = request[protocol == Protocol.Responses ? "instructions" : "system"]!.ToJsonString();
                Check(instructions.Contains("item.explanation", StringComparison.Ordinal) && instructions.Contains("attempt.error_analysis", StringComparison.Ordinal), "blank paper instructions require solutions and evidence-based error analysis");
                return Task.FromResult<List<ToolCall>>([
                    .. fixture.Document.Pages.Select(page => Read(page)),
                    Call("save_paper_info", new JsonObject { ["subject"] = "数学", ["name"] = null }),
                    Call("save_paper_info", new JsonObject { ["subject"] = "数学", ["name"] = "单元测试卷", ["possible"] = 2 }),
                    Save(question), Finish(Coverage(fixture.Document, question.DraftKey))]);
            });
            Check((await fixture.Run(api)).State == JobState.Completed, "saving a model title does not invalidate the running document snapshot");
            var result = await fixture.Library.GradingResultAsync(fixture.Document.Id);
            Check((await fixture.Library.GetDocumentAsync(fixture.Document.Id)).Name == "单元测试卷" && result.Coverage.Count == 2, "recognized title and coverage persist together");
            var saved = result.Questions.Single();
            Check(saved.Question.Items.Single() is { Attempt: null, Answer: "2", Explanation.Length: > 0 } && !saved.Question.IsMistake, "blank papers retain standard answers and solutions without student attempts");
            var entered = await fixture.Library.SaveStudentAnswersAsync(saved.Id, new Dictionary<string, string> { [question.Items[0].Key] = "1 + 1 = 3" });
            Check(AttemptOf(entered.Question) is { StudentAnswerEdited: true, ErrorAnalysis: null, Grade.Verdict: Verdict.Uncertain, Grade.Earned: null, Grade.Source: GradeSource.AiSolution }, "entering a student answer creates an ungraded response rather than a protected manual grade");
            var current = await fixture.Library.GetDocumentAsync(fixture.Document.Id);
            Check(current.Mode == ImportMode.Auto && entered.Question.Items.Single().Explanation == question.Items.Single().Explanation, "adding work to a blank paper enables grading and retains its standard solution");
            var graded = WithAttempt(question, new Attempt
            {
                StudentAnswer = "misread model text",
                ErrorAnalysis = "加法计算有误，应将两个 1 相加得到 2。",
                Grade = new Grade { Verdict = Verdict.Incorrect, Earned = 0, Possible = 2, Source = GradeSource.AiSolution }
            });
            using var http = new HttpClient(new ScriptApi(protocol, (_, _, _) => Task.FromResult<List<ToolCall>>([
                .. current.Pages.Select(page => Read(page)), Save(graded), Finish(Coverage(current, question.DraftKey))])));
            using var client = new ProviderClient(http, protocol, fixture.Input.Provider.BaseUrl);
            await using (var queue = await fixture.Queue(client))
            {
                var submitted = await queue.SubmitAsync(DiagnosticKind.Grading, Policy, fixture.Input with { Document = current });
                Check((await Wait(queue, submitted.Id)).State == JobState.Completed, "manually entered student work can be graded by the model");
            }
            var stored = (await fixture.Library.GradingQuestionsAsync(current.Id)).Single().Question;
            Check(AttemptOf(stored) is { StudentAnswer: "1 + 1 = 3", StudentAnswerEdited: true, Grade.Verdict: Verdict.Incorrect, ErrorAnalysis.Length: > 0 }, "regrading preserves the entered response and saves error analysis separately");
            await fixture.Library.UpdatePaperAsync(current.Id, "User title", "数学", null, 2);
            var protectedApi = new ScriptApi(protocol, (_, _, _) => Task.FromResult<List<ToolCall>>([
                .. current.Pages.Select(page => Read(page)),
                Call("save_paper_info", new JsonObject { ["subject"] = "数学", ["name"] = "Model title" }),
                Save(graded), Finish(Coverage(current, question.DraftKey))]));
            using var protectedHttp = new HttpClient(protectedApi);
            using var protectedClient = new ProviderClient(protectedHttp, protocol, fixture.Input.Provider.BaseUrl);
            await using var protectedQueue = await fixture.Queue(protectedClient);
            var protectedJob = await protectedQueue.SubmitAsync(DiagnosticKind.Grading, Policy, fixture.Input with { Document = current });
            Check((await Wait(protectedQueue, protectedJob.Id)).State == JobState.Completed && (await fixture.Library.GetDocumentAsync(current.Id)).Name == "User title", "recognition preserves a user-edited exam title");
        }
    }

    private static async Task SupplementaryStudentPages()
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            await using var fixture = await Fixture.Create(ImportMode.Blank, protocol);
            var question = Question(fixture.Document, reference: false);
            Check((await fixture.Run(new ScriptApi(protocol, (_, _, _) => Task.FromResult<List<ToolCall>>([
                .. fixture.Document.Pages.Select(page => Read(page)), Save(question), Finish(Coverage(fixture.Document, question.DraftKey))])))).State == JobState.Completed, "original blank paper imported before answer attachment");
            string originalId = (await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single().Id;
            var current = await fixture.Library.AppendDocumentSourcesAsync(fixture.Document.Id, [new("student.png", png, "student")]);
            var student = current.Pages.Single(page => page.Role == "student");
            var graded = WithAttempt(question, new Attempt { StudentAnswer = "2", ErrorAnalysis = null, Grade = new Grade { Verdict = Verdict.Correct, Earned = 2, Possible = 2, Source = GradeSource.AiSolution } });
            var args = JsonNode.Parse(DomainJson.Serialize(graded))!.AsObject();
            args["items"]![0]!["attempt"]!["student_page_ids"] = new JsonArray(student.Id);
            var coverage = Coverage(current, question.DraftKey).Select(c => c.PageId == student.Id ? c with { Status = "covered", Reason = "Matched student answer" } : c).ToList();
            using var http = new HttpClient(new ScriptApi(protocol, (_, _, _) => Task.FromResult<List<ToolCall>>([
                .. current.Pages.Select(page => Read(page)), Call("save_question", args), Finish(coverage)])));
            using var client = new ProviderClient(http, protocol, fixture.Input.Provider.BaseUrl);
            await using var queue = await fixture.Queue(client);
            var job = await queue.SubmitAsync(DiagnosticKind.Grading, Policy, fixture.Input with { Document = current });
            Check((await Wait(queue, job.Id)).State == JobState.Completed, "supplementary student pages participate in whole-paper grading");
            var saved = (await fixture.Library.GradingQuestionsAsync(current.Id)).Single();
            Check(saved.Id == originalId && saved.Question.Stem.SequenceEqual(question.Stem) && AttemptOf(saved.Question).StudentRegions.Single().PageId == student.Id && AttemptOf(saved.Question).ErrorAnalysis is null,
                "answer attachments reuse question identity and preserve their own source evidence without inventing error causes");
        }
    }
}
