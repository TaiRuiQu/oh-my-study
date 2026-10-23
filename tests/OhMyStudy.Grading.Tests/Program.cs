using System.Net;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Execution;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Grading;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Storage;

[assembly: SupportedOSPlatform("windows10.0.19041")]

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "OhMyStudy-Grading-" + Guid.NewGuid().ToString("N"));
    private static byte[] png = [];
    private static int checks;
    private static readonly ExecutionPolicy Policy = new() { PreferredMode = ExecutionMode.RequestPermission, EffectiveMode = ExecutionMode.RequestPermission, ExecutorKind = ExecutorKind.Host, SandboxNetworking = false };

    public static async Task<int> Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            using var assets = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", "assets.synthetic.json")));
            png = Convert.FromBase64String(assets.RootElement.GetProperty("assets")[0].GetProperty("base64").GetString()!);
            await ThreeProtocolsAndModes();
            await OptionalFields();
            await BatchedTools();
            await StreamingActivity();
            await MessagesThinkingContinuation();
            await CropAndCoverageRecovery();
            await DiscreteCoordinates();
            await CodeImageOutputs();
            await CoverageAndToolValidation();
            await InvalidToolsAndReferencePriority();
            await FinishMustBeLast();
            await ProtectedRegrading();
            await ReferencesOnly();
            await FailureRetainsSaved();
            await CancelLateModelResult();
            await SixtyRoundBound();
            await ShutdownDuringSubmission();
            Console.WriteLine($"Grading integration: {checks} checks passed. Synthetic temporary libraries only: {Root}");
            return 0;
        }
        catch (Exception error)
        {
            Console.Error.WriteLine(error);
            Console.Error.WriteLine("Retained synthetic library root: " + Root);
            return 1;
        }
    }

    private static void Check(bool condition, string label)
    {
        if (!condition) throw new InvalidOperationException(label);
        checks++;
        Console.WriteLine("PASS " + label);
    }
    private static JsonElement Element(JsonNode node) { using var doc = JsonDocument.Parse(node.ToJsonString()); return doc.RootElement.Clone(); }
    private static JsonObject Args(string key, JsonNode? value) => new() { [key] = value };
    private static ToolCall Call(string name, JsonNode arguments)
    {
        PageSources(arguments);
        if (name == "read_page") { name = "read_pages"; arguments = Args("pages", new JsonArray(arguments)); }
        if (name == "save_question") { name = "save_questions"; arguments = Args("questions", new JsonArray(arguments)); }
        return new() { Id = "synthetic-call-" + Guid.NewGuid().ToString("N"), Name = name, Arguments = Element(arguments) };
    }
    private static void PageSources(JsonNode? node)
    {
        if (node is JsonArray array) { foreach (var item in array) PageSources(item); }
        if (node is not JsonObject obj) return;
        if (obj.Remove("sources", out var sources))
            obj["source_page_ids"] = new JsonArray(sources!.AsArray().Select(region => region!["page_id"]!.GetValue<string>()).Distinct().Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        obj.Remove("student_regions");
        obj.Remove("regions");
        if (obj["region"] is JsonObject region) obj["region"] = ModelRegion(DomainJson.Deserialize<Region>(region.ToJsonString()));
        foreach (var property in obj) PageSources(property.Value);
    }
    private static ToolCall Save(Question q) => Call("save_question", JsonNode.Parse(DomainJson.Serialize(q))!);
    private static JsonObject ModelRegion(Region region) => new()
    {
        ["page_id"] = region.PageId,
        ["bbox"] = new JsonArray((int)Math.Round(region.X * 999), (int)Math.Round(region.Y * 999), (int)Math.Round((region.X + region.Width) * 999), (int)Math.Round((region.Y + region.Height) * 999))
    };
    private static ToolCall Read(SourcePage page, Region? crop = null, ushort? rotation = null) => Call("read_page", new JsonObject { ["page_id"] = page.Id, ["crop"] = crop is null ? null : ModelRegion(crop), ["rotation"] = rotation });
    private static ToolCall Refs(List<ReferenceAnswer> references) => Call("save_references", Args("references", JsonNode.Parse(DomainJson.Serialize(references))));
    private static ToolCall Finish(List<Coverage> coverage) => Call("finish", Args("coverage", JsonNode.Parse(DomainJson.Serialize(coverage))));
    private static Region Region(SourcePage page) => new() { PageId = page.Id, X = 100 / 999d, Y = 100 / 999d, Width = 500 / 999d, Height = 500 / 999d };

    private static Question Question(Document document, string key = "synthetic-q1", bool reference = true)
    {
        var region = Region(document.Pages.First(p => p.Role == "material"));
        var grade = new Grade { Verdict = Verdict.Partial, Earned = 1, Possible = 2, Source = reference ? GradeSource.AiReference : GradeSource.AiSolution, Reason = "Synthetic scoring contract" };
        if (document.Mode == ImportMode.TeacherGraded) grade = grade with { Verdict = Verdict.Incorrect, Earned = 0, Source = GradeSource.Teacher };
        return new Question
        {
            DraftKey = key,
            Number = key,
            Subject = "数学",
            QuestionType = "计算",
            Stem = [new TextContent { Text = "SYNTHETIC: 1 + 1" }],
            KnowledgePoints = ["加法"],
            Sources = [region],
            Uncertainties = [],
            Items = [new QuestionItem
            {
                Key = key,
                Points = 2,
                Answer = "2",
                Explanation = "Synthetic transport assertion only",
                Attempt = document.Mode == ImportMode.Blank ? null : new Attempt
                {
                    StudentAnswer = "synthetic answer",
                    StudentRegions = [region],
                    AiSolution = "2",
                    ReferenceKey = reference ? "synthetic-ref1" : null,
                    Teacher = document.Mode == ImportMode.TeacherGraded ? new TeacherMark { Raw = "Synthetic teacher mark", Regions = [region], Grade = grade } : null,
                    Grade = grade
                }
            }]
        };
    }
    private static Attempt AttemptOf(Question question) => question.Items[0].Attempt!;
    private static Question WithAttempt(Question question, Attempt? attempt) => question with { Items = [question.Items[0] with { Attempt = attempt }] };
    private static List<ReferenceAnswer> References(Document document, string? match = "synthetic-q1") => [new()
    {
        Key = "synthetic-ref1", Number = "1", Answer = "2", Explanation = "Synthetic reference", Sources = [Region(document.Pages.First(p => p.Role == "reference"))],
        MatchedItemKey = match, UncertainReason = match is null ? "Synthetic unmatched answer" : null
    }];
    private static List<Coverage> Coverage(Document document, params string[] keys) => document.Pages.Select(page => new Coverage
    {
        PageId = page.Id,
        Status = page.Role == "material" ? "covered" : "no_questions",
        QuestionKeys = page.Role == "material" ? keys.ToList() : [],
        Reason = page.Role == "reference" ? "Reference answer page" : ""
    }).ToList();

    private static async Task BatchedTools()
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            await using var fixture = await Fixture.Create(ImportMode.TeacherGraded, protocol, materialPages: 2);
            var first = Question(fixture.Document, reference: false);
            var second = Question(fixture.Document, "batch-q2", false) with
            {
                Sources = fixture.Document.Pages.Where(p => p.Role == "material").Select(Region).ToList(),
                Stem = [new TextContent { Text = "Diagram and formula $x^2$" }, new OhMyStudy.Domain.ImageContent { Region = Region(fixture.Document.Pages[1]) }]
            };
            var invalid = Question(fixture.Document, "batch-invalid", false) with { Stem = [] };
            var api = new ScriptApi(protocol, async (round, request, _) =>
            {
                if (round == 1)
                {
                    var pages = new JsonArray(fixture.Document.Pages.Select(page => (JsonNode)new JsonObject { ["page_id"] = page.Id }).ToArray());
                    pages.Insert(1, new JsonObject { ["page_id"] = "unknown-page" });
                    return [Call("read_pages", Args("pages", pages)), Call("save_questions", Args("questions", new JsonArray(
                        JsonNode.Parse(DomainJson.Serialize(first)), JsonNode.Parse(DomainJson.Serialize(invalid)), JsonNode.Parse(DomainJson.Serialize(second)))))];
                }
                Check(HasToolError(protocol, request), "batch reports individual read and save failures");
                Check(request.ToJsonString().Contains("image_index", StringComparison.Ordinal), "page batch identifies image attachments");
                var saved = await fixture.Library.GradingQuestionsAsync(fixture.Document.Id);
                Check(saved.Count == 2, "valid questions before and after an invalid entry remain saved");
                Check(saved.Single(q => q.Question.DraftKey == second.DraftKey).Question.Sources.Count == 2, "page IDs retain cross-page coverage without question coordinates");
                Check(saved.Single(q => q.Question.DraftKey == second.DraftKey).Question.Stem.OfType<OhMyStudy.Domain.ImageContent>().Single().Region == Region(fixture.Document.Pages[1]), "material image crop remains exact");
                return [Finish(Coverage(fixture.Document, first.DraftKey, second.DraftKey).Select(c => c.PageId == fixture.Document.Pages[1].Id ? c with { QuestionKeys = [second.DraftKey] } : c).ToList())];
            });
            Check((await fixture.Run(api)).State == JobState.Completed && api.Attempts == 2, "batched read and save complete across protocols");
            var original = (await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single(q => q.Question.DraftKey == first.DraftKey);
            var region = Region(fixture.Document.Pages[0]);
            var edited = original.Question with
            {
                Sources = [region],
                Items = [original.Question.Items[0] with
            {
                Attempt = AttemptOf(original.Question) with { StudentRegions = [region], Teacher = AttemptOf(original.Question).Teacher! with { Regions = [region] } }
            }]
            };
            await fixture.Library.EditQuestionAsync(original.Id, edited);
            var retry = new ScriptApi(protocol, (_, request, _) =>
            {
                var context = request[protocol == Protocol.Responses ? "input" : "messages"]![0]!["content"]![0]!["text"]!.GetValue<string>();
                Check(!context.Contains("student_regions", StringComparison.Ordinal) && !context.Contains("\"sources\"", StringComparison.Ordinal), "saved question context uses page IDs without answer boxes");
                return Task.FromResult<List<ToolCall>>([Call("read_pages", Args("pages", new JsonArray(fixture.Document.Pages.Select(p => (JsonNode)new JsonObject { ["page_id"] = p.Id }).ToArray()))),
                    Call("save_questions", Args("questions", new JsonArray(JsonNode.Parse(DomainJson.Serialize(first)), JsonNode.Parse(DomainJson.Serialize(second))))),
                    Finish(Coverage(fixture.Document, first.DraftKey, second.DraftKey).Select(c => c.PageId == fixture.Document.Pages[1].Id ? c with { QuestionKeys = [second.DraftKey] } : c).ToList())]);
            });
            Check((await fixture.Run(retry)).State == JobState.Completed, "batch regrading completes with existing data");
            var retained = (await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single(q => q.Id == original.Id).Question;
            Check(retained.Sources.Single() == region && AttemptOf(retained).StudentRegions.Single() == region && AttemptOf(retained).Teacher!.Regions.Single() == region,
                "page-only regrading preserves stored question and annotation regions");
        }
    }

    private static async Task OptionalFields()
    {
        await using var fixture = await Fixture.Create(ImportMode.TeacherGraded, Protocol.Responses);
        var page = fixture.Document.Pages.First(p => p.Role == "material");
        var args = new JsonObject
        {
            ["draft_key"] = "minimal-q",
            ["number"] = "1",
            ["stem"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = "一道仅有必要字段的题目。" }),
            ["sources"] = JsonNode.Parse(DomainJson.Serialize(new List<Region> { Region(page) })),
            ["items"] = new JsonArray(new JsonObject { ["key"] = "minimal-q", ["attempt"] = new JsonObject { ["grade"] = new JsonObject { ["verdict"] = "partial", ["source"] = "teacher", ["earned"] = 1, ["possible"] = 2 } } })
        };
        var parts = new JsonObject
        {
            ["draft_key"] = "multi-q",
            ["number"] = "2",
            ["stem"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = "阅读材料，回答下列问题。" }),
            ["sources"] = JsonNode.Parse(DomainJson.Serialize(new List<Region> { Region(page) })),
            ["items"] = new JsonArray(
                new JsonObject { ["key"] = "multi-q-1", ["label"] = "(1)", ["prompt"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = "第一问" }), ["points"] = 2, ["attempt"] = new JsonObject { ["grade"] = new JsonObject { ["verdict"] = "correct", ["source"] = "teacher", ["earned"] = 2, ["possible"] = 2 } } },
                new JsonObject { ["key"] = "multi-q-2", ["label"] = "(2)①", ["prompt"] = new JsonArray(new JsonObject { ["kind"] = "formula", ["latex"] = "x^2" }), ["points"] = 3, ["attempt"] = new JsonObject { ["grade"] = new JsonObject { ["verdict"] = "incorrect", ["source"] = "teacher", ["earned"] = 0, ["possible"] = 3 } } })
        };
        var api = new ScriptApi(Protocol.Responses, (_, _, _) => Task.FromResult<List<ToolCall>>([.. fixture.Document.Pages.Select(p => Read(p)), Call("save_question", args), Call("save_question", parts), Finish(Coverage(fixture.Document, "minimal-q", "multi-q"))]));
        var job = await fixture.Run(api);
        Check(job.State == JobState.Completed && api.Attempts == 1, "minimal tool fields and teacher score without evidence accepted");
        var saved = await fixture.Library.GradingQuestionsAsync(fixture.Document.Id);
        var question = saved.Single(q => q.Question.DraftKey == "minimal-q").Question;
        Check(question.Subject == "" && question.Items.Single().Options.Count == 0 && question.Items.Single().Label == "" && AttemptOf(question).Teacher is null && AttemptOf(question).Grade.Earned == 1, "omitted auxiliary fields use empty defaults without inventing evidence");
        var multi = saved.Single(q => q.Question.DraftKey == "multi-q").Question;
        Check(multi.Items.Select(item => item.Label).SequenceEqual(["(1)", "(2)①"]) && multi.Points == 5 && multi.Earned == 2 && multi.Outcome == Verdict.Partial && multi.IsMistake,
            "subquestions save as flat items with derived totals");
        Check(!GradingTools.QuestionSchema()["required"]!.ToJsonString().Contains("subject"), "schema required set is core content only");
    }

    private static async Task ThreeProtocolsAndModes()
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
            foreach (var mode in Enum.GetValues<ImportMode>())
            {
                await using var fixture = await Fixture.Create(mode, protocol);
                var question = Question(fixture.Document);
                var api = new ScriptApi(protocol, async (round, request, token) =>
                {
                    token.ThrowIfCancellationRequested();
                    AssertPromptContract(protocol, request, mode, referenceOnly: false);
                    if (round == 1)
                    {
                        Check(HasSystem(protocol, request), $"{protocol}/{mode}: native whole-paper instructions");
                        Check(request.ToJsonString().Contains("existing_questions", StringComparison.Ordinal) && request.ToJsonString().Contains("references", StringComparison.Ordinal), "whole-paper context carries saved questions/references");
                        return fixture.Document.Pages.Select(page => Read(page)).ToList();
                    }
                    Check(round == 2, "two model rounds for full paper");
                    Check(HasImageFeedback(protocol, request), $"{protocol}/{mode}: read_page returns native image result");
                    Check((await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Count == 0, "read-only round does not invent saved questions");
                    return [new ToolCall { Id = "paper-info", Name = "save_paper_info", Arguments = JsonDocument.Parse(mode == ImportMode.Blank ? "{\"subject\":\"数学\",\"earned\":null,\"possible\":100}" : "{\"subject\":\"数学\",\"earned\":86.5,\"possible\":100}").RootElement.Clone() }, Refs(References(fixture.Document)), Save(question), Save(question), Finish(Coverage(fixture.Document, question.DraftKey))];
                });
                var job = await fixture.Run(api);
                var result = await fixture.Library.GradingResultAsync(fixture.Document.Id);
                Check(job.State == JobState.Completed && job.Progress == 100 && api.Attempts == 2, $"{protocol}/{mode}: real queue completed");
                Check(result.Questions.Count == 1 && result.Questions[0].Question.DraftKey == question.DraftKey, "duplicate tool saves upsert one stable question");
                Check(result.Coverage.Count == 2 && result.References.Count == 1 && result.ModelCalls.Count == 2, "coverage, reference and call usage durably saved");
                decimal expectedCost = protocol == Protocol.Responses ? 0.0002540625m : 0.0002596875m;
                Check(result.ModelCalls.All(c => c.GetProperty("usage").GetProperty("input").GetUInt64() == 100
                    && c.GetProperty("estimated_cost").GetDecimal() == expectedCost
                    && c.GetProperty("price_cache_read").GetDouble() == 0.125 && c.GetProperty("price_cache_write").GetDouble() == 1.5625), "four-bucket call cost and price snapshot durably saved");
                var paper = (await fixture.Library.ListPapersAsync()).Single();
                Check(paper.Subject == "数学" && paper.Possible == 100 && paper.Earned == (mode == ImportMode.Blank ? null : 86.5) && paper.QuestionCount == 1, "paper metadata saved through real tool and queue");
                await fixture.Library.UpdatePaperAsync(fixture.Document.Id, "整理后的标题", "综合", null, 120);
                paper = (await fixture.Library.ListPapersAsync()).Single();
                Check(paper.Document.Name == "整理后的标题" && paper.Subject == "综合" && paper.Possible == 120 && paper.Earned is null, "manual metadata and title persist");
                bool invalid = false;
                try { await fixture.Library.UpdatePaperAsync(fixture.Document.Id, "", "数学", 101, 100); } catch (ArgumentException) { invalid = true; }
                Check(invalid && (await fixture.Library.ListPapersAsync()).Single().Document.Name == "整理后的标题", "invalid metadata does not partially update");
                var saved = result.Questions.Single().Question;
                var attempt = saved.Items.Single().Attempt;
                Check(mode == ImportMode.Blank ? attempt is null : attempt is not null, "blank/answered attempts remain distinct");
                if (mode == ImportMode.TeacherGraded) Check(attempt!.Grade == attempt.Teacher!.Grade && attempt.Grade.Source == GradeSource.Teacher, "teacher mark has priority over matched reference");
                if (mode == ImportMode.AiGrading) Check(attempt!.Grade.Source == GradeSource.AiReference && attempt.ReferenceKey == "synthetic-ref1" && GradingValidation.IsWrongQuestion(saved), "reference grading and partial enters wrong questions");
                Check(fixture.Reader.Requests.Count == 2 && fixture.Reader.Requests.All(r => r.Crop is null && r.Rotation == 0), "all originals read once without crop");
                await fixture.AssertDurableEvents(job);
                var trace = (await fixture.Library.ListJobEventsAsync(job.Id)).Where(e => e.Kind == "activity").Select(e => DomainJson.Deserialize<JobActivity>(e.Payload.GetRawText())).ToList();
                var latest = trace.GroupBy(a => a.Key).Select(g => g.Last()).ToList();
                Check(latest.Count(a => a.Kind == "model" && a.State == "completed") == 2 && latest.Any(a => a.Kind == "finish" && a.State == "completed"), "model rounds and validated finish retained in history");
                Check(latest.Max(a => a.PagesRead) == 2 && latest.Max(a => a.QuestionsSaved) == 1, "trace counts follow committed distinct pages and questions");
                Check(trace.Where(a => a.Kind == "save_questions").GroupBy(a => a.Key).All(g => g.First().State == "running" && g.Last().State == "completed"), "tool starts and committed outcomes retained");
                await fixture.Library.VerifyIntegrityAsync();
            }
    }

    private static async Task StreamingActivity()
    {
        await using var fixture = await Fixture.Create(ImportMode.Blank, Protocol.Responses);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var question = Question(fixture.Document);
        var client = new StreamingClient(async (progress, cancellation) =>
        {
            for (int i = 0; i < 100; i++) progress?.Invoke(new ProviderProgress(Text: new string('测', 220)));
            await Task.Delay(650, cancellation);
            entered.SetResult();
            await release.Task.WaitAsync(cancellation);
            return NativeTurn(Protocol.Responses, [Read(fixture.Document.Pages[0]), Read(fixture.Document.Pages[1]), Save(question), Finish(Coverage(fixture.Document, question.DraftKey))]) with { Text = new string('测', 22000) };
        });
        await using var queue = await fixture.Queue(client);
        var submitted = await queue.SubmitAsync(DiagnosticKind.Grading, Policy, fixture.Input);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var events = await fixture.Library.ListJobEventsAsync(submitted.Id);
        var partial = events.Where(e => e.Kind == "activity").Select(e => DomainJson.Deserialize<JobActivity>(e.Payload.GetRawText())).Last();
        Check(partial.State == "running" && partial.Text.Length == 16000 && (await queue.GetAsync(submitted.Id)).State == JobState.Running, "bounded public output visible before model completion");
        Check(events.Count(e => e.Kind == "activity") <= 3, "stream deltas coalesced instead of writing every token");
        release.SetResult();
        var job = await Wait(queue, submitted.Id);
        var completed = (await fixture.Library.ListJobEventsAsync(job.Id)).Where(e => e.Kind == "activity").Select(e => DomainJson.Deserialize<JobActivity>(e.Payload.GetRawText())).ToList();
        Check(job.State == JobState.Completed && completed.All(a => a.Text.Length <= 16000) && completed.Any(a => a.Kind == "model" && a.State == "completed"), "bounded final output and tool history survive completion");
        var failedClient = new StreamingClient((progress, _) =>
        {
            progress?.Invoke(new ProviderProgress(Text: "连接中断前已收到的公开回复"));
            throw new HttpRequestException("Synthetic disconnected stream");
        });
        var failed = await fixture.Run(failedClient);
        var retained = (await fixture.Library.ListJobEventsAsync(failed.Id)).Where(e => e.Kind == "activity").Select(e => DomainJson.Deserialize<JobActivity>(e.Payload.GetRawText())).ToList();
        Check(failed.State == JobState.Failed && retained.Any(a => a.Text == "连接中断前已收到的公开回复"
            && a.State == "failed" && a.Detail.Contains("network", StringComparison.Ordinal)), "short partial response and actionable error retained on immediate transport failure");
    }

    private static async Task MessagesThinkingContinuation()
    {
        await using var fixture = await Fixture.Create(ImportMode.Blank, Protocol.Messages);
        var thinking = new AdaptiveThinking { Effort = "medium" };
        var input = fixture.Input with
        {
            Model = fixture.Input.Model with { Id = "claude-haiku-5-5", MaxOutputTokens = 24576 }
        };
        input = input with { Model = input.Model with { Capabilities = input.Model.Capabilities with { Thinking = [thinking] } }, Thinking = thinking };
        JsonNode? priorMessages = null;
        JsonNode? priorTools = null;
        JsonNode? priorSystem = null;
        var question = Question(fixture.Document);
        var api = new ScriptApi(Protocol.Messages, (attempt, body, _) =>
        {
            Check(body["thinking"]?["type"]?.ToString() == "adaptive" && body["output_config"]?["effort"]?.ToString() == "medium", "Haiku medium uses adaptive thinking on every turn");
            Check(body["max_tokens"]!.GetValue<int>() == 24576, "thinking has output headroom within configured model limit");
            if (priorMessages is not null)
            {
                var messages = body["messages"]!.AsArray();
                Check(priorMessages.AsArray().Select((item, index) => JsonNode.DeepEquals(item, messages[index])).All(equal => equal)
                    && JsonNode.DeepEquals(priorTools, body["tools"]) && JsonNode.DeepEquals(priorSystem, body["system"]), "thinking continuation preserves the signed conversation prefix, tools and system");
                Check(messages[^1]!["role"]!.ToString() == "user" && messages[^1]!["content"]!.AsArray().Count > 0, "continuation has a nonempty user turn");
                Check(messages[^2]!["content"]![0]!["signature"]!.ToString() == "SYNTHETIC_NEVER_SEND_EXTERNALLY", "thinking signature is preserved across continuation");
            }
            priorMessages = body["messages"]!.DeepClone(); priorTools = body["tools"]!.DeepClone(); priorSystem = body["system"]!.DeepClone();
            return Task.FromResult<List<ToolCall>>(attempt switch
            {
                1 => fixture.Document.Pages.Select(p => Read(p)).ToList(),
                2 => [],
                3 => [Save(question), Finish(Coverage(fixture.Document, question.DraftKey))],
                _ => throw new InvalidOperationException("Unexpected continuation")
            });
        });
        using var http = new HttpClient(api);
        using var client = new ProviderClient(http, Protocol.Messages, input.Provider.BaseUrl);
        await using var queue = await fixture.Queue(client);
        var submitted = await queue.SubmitAsync(DiagnosticKind.Grading, Policy, input);
        var completed = await Wait(queue, submitted.Id);
        Check(completed.State == JobState.Completed && api.Attempts == 3
            && (await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Count == 1, "text-only model reply continues to saved results with medium thinking");
        var stalled = new DelegateClient(Protocol.Messages, (_, _, _) => Task.FromResult(NativeTurn(Protocol.Messages, [])));
        var failed = await fixture.Run(stalled);
        Check(failed.State == JobState.Failed && stalled.Attempts == 3, "text-only continuations remain bounded");
    }

    private sealed class StreamingClient(Func<Action<ProviderProgress>?, CancellationToken, Task<Turn>> call) : IModelClient
    {
        public Protocol Protocol => Protocol.Responses;
        public Task<Turn> CallAsync(ProviderCredentials credentials, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools, Action<ProviderProgress>? progress = null, CancellationToken cancellationToken = default) => call(progress, cancellationToken);
        public Task<ModelPage> ListModelsAsync(ProviderCredentials credentials, string? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private static bool HasSystem(Protocol protocol, JsonObject request) => protocol switch
    { Protocol.Responses => request["instructions"] is not null, Protocol.Messages => request["system"] is not null, _ => request["systemInstruction"] is not null };

    private static void AssertPromptContract(Protocol protocol, JsonObject request, ImportMode mode, bool referenceOnly)
    {
        var instructions = request[protocol == Protocol.Responses ? "instructions" : "system"]!.GetValue<string>();
        Check(instructions.All(char.IsAscii) && instructions.StartsWith("Organize the exam", StringComparison.Ordinal), "native high-priority instructions are structured English");
        Check(instructions.Contains("Simplified Chinese", StringComparison.Ordinal)
            && instructions.Contains("Preserve the source language", StringComparison.Ordinal), "English instructions preserve the content-language contract");
        var task = new[] { "", instructions[(instructions.LastIndexOf("\n\n", StringComparison.Ordinal) + 2)..] };
        Check(task.Length == 2 && task[1].StartsWith(referenceOnly ? "Organize reference answers only." : mode switch
        {
            ImportMode.TeacherGraded => "Organize and grade the whole paper.",
            ImportMode.Blank => "Organize a blank paper without grading.",
            _ => "Organize and grade the whole answered paper."
        }, StringComparison.Ordinal), "native instructions select exactly the current task mode");
        var messages = request[protocol == Protocol.Responses ? "input" : "messages"]!.AsArray();
        var first = messages[0]!.AsObject();
        var input = first["content"]!.AsArray()[0]!["text"]!.GetValue<string>();
        int contextStart = input.IndexOf('{');
        Check(first["role"]!.GetValue<string>() == "user" && contextStart > 0 && input[..contextStart].All(char.IsAscii), "dynamic context is framed separately in a user message");
        using var context = JsonDocument.Parse(input[contextStart..]);
        var expectedMode = mode switch
        {
            ImportMode.TeacherGraded => "teacher_graded",
            ImportMode.Blank => "blank",
            _ => "ai_grading"
        };
        Check(context.RootElement.GetProperty("document").GetProperty("mode").GetString() == expectedMode
            && context.RootElement.GetProperty("existing_questions").ValueKind == JsonValueKind.Array
            && context.RootElement.GetProperty("references").ValueKind == JsonValueKind.Array, "typed task context remains valid JSON in both protocols");

        var tools = request["tools"]!.AsArray().Select(t => t!.AsObject()).ToList();
        Check(tools.Count == (referenceOnly ? 5 : 7), "task exposes only its supported tools");
        foreach (var tool in tools)
        {
            string name = tool["name"]!.GetValue<string>();
            string description = tool["description"]!.GetValue<string>();
            Check(!string.IsNullOrWhiteSpace(description) && description.All(char.IsAscii), name + ": English description survives SDK serialization");
            var schema = tool[protocol == Protocol.Responses ? "parameters" : "input_schema"]!.AsObject();
            Check(DescribedClosedSchema(schema), name + ": nested parameter descriptions and closed objects survive SDK serialization without references");
            if (protocol == Protocol.Responses) Check(tool["strict"]!.GetValue<bool>() == false, name + ": omission semantics remain available");
            if (name == "save_questions")
                Check(schema["properties"]!["questions"]!["items"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).ToHashSet().SetEquals(["draft_key", "number", "stem", "source_page_ids", "items"])
                    && schema["properties"]!["questions"]!["items"]!["properties"]!["items"]!["items"]!["required"]!.AsArray().Select(n => n!.GetValue<string>()).SequenceEqual(["key"])
                    && schema["properties"]!["questions"]!["items"]!["properties"]!["items"]!["items"]!["properties"]!["attempt"]!["anyOf"]![0]!["required"]!.AsArray().Count == 0,
                    "question, item and attempt auxiliary fields remain optional for regrading");
        }
    }

    private static bool DescribedClosedSchema(JsonNode? node)
    {
        if (node is JsonArray array) return array.All(DescribedClosedSchema);
        if (node is not JsonObject obj) return true;
        if (obj.ContainsKey("$ref") || obj.ContainsKey("$defs")) return false;
        if (obj["type"]?.ToString() == "object" && obj["additionalProperties"]?.GetValue<bool>() != false) return false;
        if (obj["properties"] is JsonObject properties && properties.Any(p =>
            p.Value?["description"] is not JsonValue description || !description.TryGetValue<string>(out var text)
                || string.IsNullOrWhiteSpace(text) || !text.All(char.IsAscii))) return false;
        return obj.All(p => DescribedClosedSchema(p.Value));
    }
    private static bool HasImageFeedback(Protocol protocol, JsonObject request) => protocol switch
    {
        Protocol.Responses => ((JsonArray)request["input"]!).OfType<JsonObject>().Any(item => item["type"]?.ToString() == "function_call_output" && item["output"] is JsonArray blocks && blocks.OfType<JsonObject>().Any(p => p["type"]?.ToString() == "input_image")),
        Protocol.Messages => ((JsonArray)request["messages"]!).OfType<JsonObject>().Any(item => item["content"] is JsonArray blocks && blocks.OfType<JsonObject>().Any(p => p["type"]?.ToString() == "tool_result" && p["content"] is JsonArray nested && nested.OfType<JsonObject>().Any(n => n["type"]?.ToString() == "image"))),
        _ => throw new InvalidOperationException()
    };
    private static bool HasToolError(Protocol protocol, JsonObject request)
    {
        var text = request.ToJsonString();
        return protocol switch
        {
            Protocol.Messages => text.Contains("\"is_error\":true", StringComparison.Ordinal),
            Protocol.Responses => text.Contains("Tool execution failed:", StringComparison.Ordinal),
            _ => throw new InvalidOperationException()
        };
    }

    private static async Task CropAndCoverageRecovery()
    {
        await using var fixture = await Fixture.Create(ImportMode.AiGrading, Protocol.Messages);
        var question = Question(fixture.Document, reference: false);
        var material = fixture.Document.Pages[0];
        var api = new ScriptApi(Protocol.Messages, async (round, request, _) =>
        {
            if (round == 1) return [Read(material, Region(material), 90), Read(fixture.Document.Pages[1]), Save(question), Finish(Coverage(fixture.Document, question.DraftKey))];
            Check(HasToolError(Protocol.Messages, request), "cropped page cannot count as full-page read");
            Check((await fixture.Library.GradingResultAsync(fixture.Document.Id)).Coverage.Count == 0, "failed coverage does not persist");
            Check((await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Count == 1, "coverage failure retains saved question");
            return [Read(material), Finish(Coverage(fixture.Document, question.DraftKey))];
        });
        var job = await fixture.Run(api);
        Check(job.State == JobState.Completed && api.Attempts == 2, "full-page reread repairs coverage");
        Check(fixture.Reader.Requests.Count == 3 && fixture.Reader.Requests[0].Crop == Region(material) && fixture.Reader.Requests[0].Rotation == 90, "crop and rotation reach image reader unchanged");
    }

    private static IEnumerable<JsonObject> FeedbackObjects(JsonNode? node)
    {
        if (node is JsonObject obj)
        {
            yield return obj;
            foreach (var property in obj)
                foreach (var nested in FeedbackObjects(property.Value)) yield return nested;
        }
        else if (node is JsonArray array)
        {
            foreach (var item in array)
                foreach (var nested in FeedbackObjects(item)) yield return nested;
        }
        else if (node is JsonValue value && value.TryGetValue<string>(out var text))
        {
            const string prefix = "Tool execution failed: ";
            if (text.StartsWith(prefix, StringComparison.Ordinal)) text = text[prefix.Length..];
            if (!text.StartsWith('{')) yield break;
            JsonNode? parsed;
            try { parsed = JsonNode.Parse(text); }
            catch (JsonException) { yield break; }
            foreach (var nested in FeedbackObjects(parsed)) yield return nested;
        }
    }

    private static async Task DiscreteCoordinates()
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            await using var fixture = await Fixture.Create(ImportMode.AiGrading, protocol);
            var page = fixture.Document.Pages[0];
            var full = new Region { PageId = page.Id, X = 0, Y = 0, Width = 1, Height = 1 };
            var question = Question(fixture.Document, reference: false) with { Stem = [new OhMyStudy.Domain.ImageContent { Region = full }] };
            var client = new DelegateClient(protocol, (round, history, _) =>
            {
                List<ToolCall> calls;
                if (round == 1)
                {
                    var pages = new JsonArray();
                    foreach (var bbox in new[] { new JsonArray(0, 0, 999, 999), new JsonArray(0, 0, 1.5, 999), new JsonArray(500, 0, 100, 999), new JsonArray(0, 0, 1000, 999), new JsonArray(0, 0, 0, 999) })
                        pages.Add((JsonNode)new JsonObject { ["page_id"] = page.Id, ["crop"] = new JsonObject { ["page_id"] = page.Id, ["bbox"] = bbox } });
                    calls = [Call("read_pages", Args("pages", pages)), .. fixture.Document.Pages.Select(value => Read(value)), Save(question)];
                }
                else
                {
                    var feedback = FeedbackObjects(history).ToList();
                    Check(feedback.Any(value => value["error"]?.ToString() == "4 entries failed. Successful entries were retained; retry only the failed entries."), "fractional, reversed, out-of-range and empty discrete boxes are rejected");
                    Check(fixture.Reader.Requests.First().Crop == full, "0..999 full-page box converts to the entire original image");
                    calls = [Finish(Coverage(fixture.Document, question.DraftKey))];
                }
                return Task.FromResult(NativeTurn(protocol, calls));
            });
            Check((await fixture.Run(client)).State == JobState.Completed, "discrete region validation recovers across provider protocols");
            Check((await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single().Question.Stem.OfType<OhMyStudy.Domain.ImageContent>().Single().Region == full, "discrete image box persists in the compatible normalized storage format");
        }
    }

    private static async Task CodeImageOutputs()
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            await using var fixture = await Fixture.Create(ImportMode.AiGrading, protocol);
            var executor = new OutputExecutor(); fixture.CodeExecutor = executor;
            string? assetId = null;
            var question = Question(fixture.Document, reference: false);
            var client = new DelegateClient(protocol, (round, history, _) =>
            {
                var feedback = FeedbackObjects(history).ToList();
                List<ToolCall> calls;
                if (round == 1) calls = fixture.Document.Pages.Select(page => Read(page)).ToList();
                else if (round == 2)
                {
                    executor.Input = feedback.First(value => value["local_file"] is not null)["local_file"]!.GetValue<string>();
                    Check(feedback.Any(value => value["coordinate_system"]?.ToString() == "unrotated_source_discrete_0_999"), "page feedback declares discrete coordinates");
                    calls = [Call("python", new JsonObject { ["code"] = "synthetic failure" })];
                }
                else if (round == 3)
                {
                    Check(feedback.Any(value => value["stderr"]?.ToString() == "synthetic actionable traceback" && value["stdout"]?.ToString() == "partial output" && value["exit_code"]?.GetValue<uint>() == 1), "failed code returns status, stdout, stderr and exit code for recovery");
                    calls = [Call("python", new JsonObject { ["code"] = "must not execute", ["outputs"] = new JsonArray("../escape.png") }),
                        Call("python", new JsonObject { ["code"] = "must not execute", ["timeout_ms"] = -1 }),
                        Call("shell", new JsonObject { ["code"] = "startup failure" }),
                        Call("shell", new JsonObject { ["code"] = "synthetic image output", ["outputs"] = new JsonArray("processed.png", "missing.png"), ["timeout_ms"] = 120000 })];
                }
                else if (round == 4)
                {
                    assetId = feedback.Single(value => value["path"]?.ToString() == "processed.png" && value["asset_id"] is not null)["asset_id"]!.GetValue<string>();
                    Check(feedback.Any(value => value["path"]?.ToString() == "missing.png" && value["error"] is not null), "missing output returns a recoverable per-image error");
                    Check(feedback.Any(value => value["stderr"]?.ToString() == "synthetic worker startup failure"), "worker startup errors are returned to the model without switching executors");
                    var image = new OhMyStudy.Domain.ImageContent { Region = Region(fixture.Document.Pages[0]), AssetId = assetId, Caption = "Processed diagram" };
                    question = question with { Stem = [new TextContent { Text = "Question with processed image" }, image] };
                    var invalid = question with { DraftKey = "invalid-image", Items = [question.Items[0] with { Key = "invalid-image" }], Stem = [image with { AssetId = Guid.NewGuid().ToString() }] };
                    calls = [Save(invalid), Save(question)];
                }
                else calls = [Finish(Coverage(fixture.Document, question.DraftKey))];
                return Task.FromResult(NativeTurn(protocol, calls));
            });
            await using var queue = await fixture.Queue(client);
            var submitted = await queue.SubmitAsync(DiagnosticKind.Grading, Policy with { PreferredMode = ExecutionMode.BypassPermission, EffectiveMode = ExecutionMode.BypassPermission }, fixture.Input);
            var job = await Wait(queue, submitted.Id);
            Check(job.State == JobState.Completed && executor.Calls == 3, "code processing recovers and rejects invalid output paths and timeouts before execution");
            var saved = (await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single();
            var stored = saved.Question.Stem.OfType<OhMyStudy.Domain.ImageContent>().Single();
            Check(stored.AssetId == assetId && stored.Region == Region(fixture.Document.Pages[0]), "processed asset and original evidence region persist together");
            Check((await fixture.Library.ReadAssetAsync(assetId!)).Bytes.SequenceEqual(png), "processed image remains readable from the library");
            var edited = await fixture.Library.EditQuestionAsync(saved.Id, saved.Question with { Number = "edited" });
            Check(edited.Question.Stem.OfType<OhMyStudy.Domain.ImageContent>().Single().AssetId == assetId, "editing retains the processed image");
            var events = await fixture.Library.ListJobEventsAsync(job.Id);
            Check(events.Any(value => value.Payload.GetRawText().Contains("synthetic actionable traceback", StringComparison.Ordinal)), "execution failure details remain visible in task activities");
            await fixture.Library.DeletePaperAsync(fixture.Document.Id);
            try { await fixture.Library.ReadAssetAsync(assetId!); throw new InvalidOperationException("Processed asset survived paper deletion"); }
            catch (KeyNotFoundException) { }
            Check((await fixture.Library.RecoverOrphanedAssetsAsync(false)).Count == 0, "deleting the paper removes generated assets and files");
        }
    }

    private sealed class OutputExecutor : IToolExecutor
    {
        public int Calls { get; private set; }
        public string Input { get; set; } = "";
        public async Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall authorization, string resultsDirectory, CancellationToken cancellationToken)
        {
            var call = authorization.Consume(); Calls++;
            Check(File.Exists(Path.Combine(call.WorkingDirectory, Input)), "read_pages exposes a real local input file");
            if (call.Content == "startup failure") throw new IOException("synthetic worker startup failure");
            if (call.Tool == ToolKind.Python)
            {
                await File.WriteAllTextAsync(Path.Combine(call.WorkingDirectory, "scratch.txt"), "preserved", cancellationToken);
                return new("failed", 1, "partial output", "synthetic actionable traceback");
            }
            Check(File.ReadAllText(Path.Combine(call.WorkingDirectory, "scratch.txt")) == "preserved" && call.TimeoutMs == 120000, "code files persist between tools and requested timeout is honored");
            File.Copy(Path.Combine(call.WorkingDirectory, Input), Path.Combine(call.WorkingDirectory, "processed.png"));
            return new("completed", 0, "image saved", "");
        }
    }

    private static async Task CoverageAndToolValidation()
    {
        await using var fixture = await Fixture.Create(ImportMode.AiGrading, Protocol.Messages, materialPages: 2);
        var question = Question(fixture.Document, reference: false);
        var valid = Coverage(fixture.Document, question.DraftKey);
        // q1 spans both pages; a page that lists q1 without a source must be rejected first.
        var invalid = new List<List<Coverage>>
        {
            valid.Take(1).ToList(),
            new() { valid[0], valid[0], valid[2] },
            valid.Select(c => c with { QuestionKeys = c.QuestionKeys.Count == 0 ? [] : ["nonexistent"] }).ToList(),
            valid,
            valid.Select(c => c.PageId == fixture.Document.Pages[1].Id ? c with { Status = "unreadable", QuestionKeys = [], Reason = "" } : c).ToList()
        };
        var api = new ScriptApi(Protocol.Messages, async (round, request, _) =>
        {
            if (round == 1) return [.. fixture.Document.Pages.Select(page => Read(page)), Save(question), Finish(invalid[0])];
            Check(HasToolError(Protocol.Messages, request), "invalid coverage returns tool error");
            Check((await fixture.Library.GradingResultAsync(fixture.Document.Id)).Coverage.Count == 0, "invalid coverage stays uncommitted");
            if (round <= invalid.Count) return [Finish(invalid[round - 1])];
            var spanning = question with { Sources = [.. question.Sources, Region(fixture.Document.Pages[1])] };
            return [Save(spanning), Finish(valid)];
        });
        var job = await fixture.Run(api);
        Check(job.State == JobState.Completed && api.Attempts == invalid.Count + 1, "missing, duplicate, unknown, wrong-source and unexplained coverage repaired");
        Check((await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single().Question.Sources.Count == 2, "cross-page question preserves both sources");
    }

    private static async Task FinishMustBeLast()
    {
        await using var fixture = await Fixture.Create(ImportMode.AiGrading, Protocol.Responses);
        var first = Question(fixture.Document, reference: false);
        var second = Question(fixture.Document, "synthetic-q2", false);
        var api = new ScriptApi(Protocol.Responses, async (round, request, _) =>
        {
            if (round == 1) return [.. fixture.Document.Pages.Select(p => Read(p)), Save(first)];
            if (round == 2) return [Finish(Coverage(fixture.Document, first.DraftKey)), Save(second)];
            Check(HasToolError(Protocol.Responses, request), "finish before later tool is rejected");
            var result = await fixture.Library.GradingResultAsync(fixture.Document.Id);
            Check(result.Coverage.Count == 0 && result.Questions.Count == 2, "early finish cannot prematurely complete or drop later save");
            return [Finish(Coverage(fixture.Document, first.DraftKey, second.DraftKey))];
        });
        var job = await fixture.Run(api);
        Check(job.State == JobState.Completed && api.Attempts == 3, "correct terminal finish completes after recovery");
    }

    private static async Task ProtectedRegrading()
    {
        foreach (var teacher in new[] { false, true })
        {
            await using var fixture = await Fixture.Create(teacher ? ImportMode.TeacherGraded : ImportMode.AiGrading, Protocol.Responses);
            var original = Question(fixture.Document, reference: false);
            var first = new ScriptApi(Protocol.Responses, (_, _, _) => Task.FromResult<List<ToolCall>>([.. fixture.Document.Pages.Select(p => Read(p)), Save(original), Finish(Coverage(fixture.Document, original.DraftKey))]));
            Check((await fixture.Run(first)).State == JobState.Completed, "initial protected-grade paper completed");
            var saved = (await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single();
            if (!teacher) saved = await fixture.Library.EditQuestionAsync(saved.Id, WithAttempt(saved.Question, AttemptOf(saved.Question) with { Grade = AttemptOf(saved.Question).Grade with { Verdict = Verdict.Incorrect, Earned = 0 } }));
            var protectedGrade = AttemptOf(saved.Question).Grade;
            var incoming = WithAttempt(original, AttemptOf(original) with { Teacher = null, Grade = AttemptOf(original).Grade with { Verdict = Verdict.Correct, Earned = 2, Source = GradeSource.AiSolution } });
            saved = await fixture.Library.SaveQuestionNotesAsync(saved.Id, "PRIVATE-REVIEW-NOTE");
            var retry = new ScriptApi(Protocol.Responses, async (round, request, _) =>
            {
                Check(!request.ToJsonString().Contains("PRIVATE-REVIEW-NOTE", StringComparison.Ordinal), "private notes are never sent to the provider");
                if (round == 1) return [.. fixture.Document.Pages.Select(p => Read(p)), Save(WithAttempt(incoming, null))];
                Check(HasToolError(Protocol.Responses, request), "explicit null cannot remove protected attempt");
                Check(AttemptOf((await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single().Question).Grade == protectedGrade, "failed removal retains old grade");
                return [Save(incoming), Finish(Coverage(fixture.Document, incoming.DraftKey))];
            });
            Check((await fixture.Run(retry)).State == JobState.Completed, "protected regrade recovers and completes");
            var final = (await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Single();
            Check(final.Id == saved.Id && AttemptOf(final.Question).Grade == protectedGrade, teacher ? "teacher original grade survives AI regrade" : "manual grade survives AI regrade with same saved ID");
            if (teacher) Check(DomainJson.Serialize(AttemptOf(final.Question).Teacher) == DomainJson.Serialize(AttemptOf(saved.Question).Teacher), "teacher evidence survives AI replacement");
            Check(final.Question.Notes == "PRIVATE-REVIEW-NOTE", "regrade keeps private notes");
        }
    }

    private static async Task InvalidToolsAndReferencePriority()
    {
        await using var fixture = await Fixture.Create(ImportMode.AiGrading, Protocol.Responses);
        var question = Question(fixture.Document);
        var page = fixture.Document.Pages[0];
        var forgedManual = WithAttempt(question, AttemptOf(question) with { Grade = AttemptOf(question).Grade with { Source = GradeSource.Manual } });
        var bypass = WithAttempt(question, AttemptOf(question) with { ReferenceKey = null, Grade = AttemptOf(question).Grade with { Source = GradeSource.AiSolution } });
        var api = new ScriptApi(Protocol.Responses, async (round, request, _) =>
        {
            if (round == 1) return
            [
                Call("read_page", new JsonObject { ["page_id"] = Guid.NewGuid().ToString() }),
                Read(page, Region(page) with { PageId = fixture.Document.Pages[1].Id }),
                Read(page, Region(page) with { X = 0.9 }),
                Read(page, rotation: 45),
                Call("read_page", new JsonObject { ["page_id"] = page.Id, ["rotation"] = -1 }),
                Call("read_page", new JsonObject { ["page_id"] = page.Id, ["unknown"] = true }),
                .. fixture.Document.Pages.Select(p => Read(p)), Refs(References(fixture.Document)), Save(forgedManual)
            ];
            Check(HasToolError(Protocol.Responses, request), "invalid read or save produces recoverable tool feedback");
            Check((await fixture.Library.GradingQuestionsAsync(fixture.Document.Id)).Count == 0, "invalid model grade never creates question row");
            if (round == 2) return [Save(bypass)];
            return [Save(question), Finish(Coverage(fixture.Document, question.DraftKey))];
        });
        var job = await fixture.Run(api);
        var result = await fixture.Library.GradingResultAsync(fixture.Document.Id);
        Check(job.State == JobState.Completed && api.Attempts == 3 && fixture.Reader.Requests.Count == 2, "invalid page, crop, rotation, field and grade recover without extra image reads");
        Check(AttemptOf(result.Questions.Single().Question).Grade.Source == GradeSource.AiReference, "matched reference cannot be bypassed by AI source");
    }

    private static async Task ReferencesOnly()
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            await using var fixture = await Fixture.Create(ImportMode.AiGrading, protocol);
            var api = new ScriptApi(protocol, (round, request, _) =>
            {
                AssertPromptContract(protocol, request, ImportMode.AiGrading, referenceOnly: true);
                var definitions = (JsonArray)request["tools"]!;
                Check(!definitions.OfType<JsonObject>().Any(t => t["name"]?.ToString() is "save_questions" or "save_paper_info"), "reference-only schema excludes grading tools");
                if (round == 1) return Task.FromResult<List<ToolCall>>([Save(Question(fixture.Document, reference: false)), Call("unknown", new JsonObject { ["code"] = "print('not executed')" })]);
                Check(HasToolError(protocol, request), "reference-only task rejects unexpected tools");
                var page = fixture.Document.Pages.Single(p => p.Role == "reference");
                return Task.FromResult<List<ToolCall>>([Read(page), Refs(References(fixture.Document, match: null)), Finish([new() { PageId = page.Id, Status = "no_questions", QuestionKeys = [], Reason = "Only reference answers" }])]);
            });
            var job = await fixture.Run(api, DiagnosticKind.References);
            var result = await fixture.Library.GradingResultAsync(fixture.Document.Id);
            Check(job.State == JobState.Completed && result.Questions.Count == 0 && result.References.Count == 1 && result.Coverage.Count == 1, "reference task saves answers and reference-page-only coverage");
            Check(fixture.Reader.Requests.Count == 1 && fixture.Executor.Calls == 0, "reference task never reads material or executes code");
        }
    }

    private static async Task FailureRetainsSaved()
    {
        foreach (var noTools in new[] { false, true })
        {
            await using var fixture = await Fixture.Create(ImportMode.AiGrading, Protocol.Responses);
            var question = Question(fixture.Document, reference: false);
            var client = new DelegateClient(Protocol.Responses, (round, _, _) =>
            {
                if (round == 1) return Task.FromResult(NativeTurn(Protocol.Responses, [.. fixture.Document.Pages.Select(p => Read(p)), Save(question)]));
                if (!noTools) throw new ProviderException(ErrorKind.Network, "synthetic interrupted response");
                return Task.FromResult(NativeTurn(Protocol.Responses, []));
            });
            var job = await fixture.Run(client);
            var result = await fixture.Library.GradingResultAsync(fixture.Document.Id);
            Check(job.State == JobState.Failed && result.Questions.Count == 1 && result.Coverage.Count == 0, noTools ? "plain model text cannot complete and previous save remains" : "network failure preserves durable previous result");
            Check(client.Attempts == (noTools ? 4 : 2) && result.ModelCalls.Count == (noTools ? 4 : 1),
                noTools ? "bounded text continuations account for every model call" : "no hidden retry or fabricated usage on network failure");
            await fixture.AssertDurableEvents(job);
        }
    }

    private static async Task CancelLateModelResult()
    {
        await using var fixture = await Fixture.Create(ImportMode.AiGrading, Protocol.Responses);
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = Question(fixture.Document, reference: false);
        var late = Question(fixture.Document, "synthetic-late", false);
        var client = new DelegateClient(Protocol.Responses, async (round, _, _) =>
        {
            if (round == 1) return NativeTurn(Protocol.Responses, [Save(first)]);
            waiting.TrySetResult();
            await release.Task; // Deliberately hostile transport ignores cancellation, testing the commit barrier.
            return NativeTurn(Protocol.Responses, [Save(late), Finish(Coverage(fixture.Document, first.DraftKey, late.DraftKey))]);
        });
        await using var queue = await fixture.Queue(client);
        var submitted = await queue.SubmitAsync(DiagnosticKind.Grading, Policy, fixture.Input);
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var cancelled = await queue.CancelAsync(submitted.Id);
        var eventCount = (await fixture.Library.ListJobEventsAsync(submitted.Id)).Count;
        release.TrySetResult();
        // A new storage diagnostic runs only once the cancelled runner has actually left the execution slot.
        var sentinel = await queue.SubmitAsync(DiagnosticKind.Storage, Policy, null);
        Check((await Wait(queue, sentinel.Id)).State == JobState.Completed, "cancelled runner releases queue after late result returns");
        var result = await fixture.Library.GradingResultAsync(fixture.Document.Id);
        Check(cancelled.State == JobState.Cancelled && (await queue.GetAsync(submitted.Id)).State == JobState.Cancelled, "late result cannot change terminal cancellation");
        Check(result.Questions.Count == 1 && result.Questions.Single().Question.DraftKey == first.DraftKey && result.ModelCalls.Count == 1 && result.Coverage.Count == 0, "late save, usage and finish all blocked after cancellation");
        Check((await fixture.Library.ListJobEventsAsync(submitted.Id)).Count == eventCount, "cancelled job gains no late progress or completion event");
    }

    private static async Task SixtyRoundBound()
    {
        await using var fixture = await Fixture.Create(ImportMode.Blank, Protocol.Responses);
        var client = new DelegateClient(Protocol.Responses, (_, _, _) => Task.FromResult(NativeTurn(Protocol.Responses, [Call("unsupported_tool", new JsonObject())])));
        var job = await fixture.Run(client);
        var result = await fixture.Library.GradingResultAsync(fixture.Document.Id);
        Check(job.State == JobState.Failed && client.Attempts == 60 && result.ModelCalls.Count == 60, "tool loop stops at exactly 60 native rounds");
        Check(result.Questions.Count == 0 && result.Coverage.Count == 0, "exhausted loop does not fabricate paper completion");
    }

    private static async Task ShutdownDuringSubmission()
    {
        await using var fixture = await Fixture.Create(ImportMode.Blank, Protocol.Responses);
        var store = new BlockingJobStore(new RepositoryJobStore(fixture.Library));
        var client = new DelegateClient(Protocol.Responses, (_, _, _) => throw new InvalidOperationException("No model call expected"));
        var runner = new GradingRunner(fixture.Library, fixture.Reader, _ => client, _ => new ProviderCredentials("synthetic-key"));
        var queue = await JobQueue.CreateAsync(store, runner, fixture.Executor, Path.Combine(Root, "shutdown", Guid.NewGuid().ToString("N")));
        var submitting = queue.SubmitAsync(DiagnosticKind.Storage, Policy, null);
        await store.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        var stopping = queue.DisposeAsync().AsTask();
        await Task.Delay(50);
        bool returnedBeforeSubmissionSettled = stopping.IsCompleted;
        store.Release.TrySetResult();
        try { await submitting; } catch (InvalidOperationException) { /* A shutdown may reject a pending submission. */ }
        await stopping;
        Check(!returnedBeforeSubmissionSettled, "shutdown waits for in-flight submission persistence");
        var snapshots = await fixture.Library.LoadJobSnapshotsAsync();
        Check(snapshots.All(job => job.State.IsTerminal()), "shutdown leaves no newly persisted queued job behind");
        Check(client.Attempts == 0 && fixture.Executor.Calls == 0, "shutdown race cannot trigger model or tool execution");
    }

    private static async Task<Job> Wait(JobQueue queue, string id)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        while (true)
        {
            var job = await queue.GetAsync(id, timeout.Token);
            if (job.State.IsTerminal()) return job;
            await Task.Delay(10, timeout.Token);
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public required LibraryRepository Library { get; init; }
        public required Document Document { get; init; }
        public required GradingInput Input { get; init; }
        public PixelReader Reader { get; } = new();
        public RejectExecutor Executor { get; } = new();
        public IToolExecutor? CodeExecutor { get; set; }
        public static async Task<Fixture> Create(ImportMode mode, Protocol protocol, int materialPages = 1)
        {
            var repository = await LibraryRepository.OpenAsync(new(Path.Combine(Root, Guid.NewGuid().ToString("N")), LibraryOpenMode.CreateNew) { MediaProcessor = new PixelProcessor() });
            var sources = Enumerable.Range(0, materialPages).Select(_ => new ImportSource("synthetic material.png", png)).Append(new("synthetic reference.png", png, "reference")).ToList();
            var document = await repository.ImportDocumentAsync("Synthetic integration paper", mode, sources);
            var model = new ModelConfig { Id = "synthetic-model", Capabilities = Capabilities.Unknown with { Vision = true, Tools = true }, PriceInput = 1.25, PriceCacheRead = 0.125, PriceCacheWrite = 1.5625, PriceOutput = 5, Currency = "USD" };
            return new Fixture
            {
                Library = repository,
                Document = document,
                Input = new() { Document = document, Provider = new() { Id = Guid.NewGuid().ToString(), Name = "Synthetic fake handler", Protocol = protocol, BaseUrl = "https://example.invalid/v1/", KeyRef = null, Models = [model] }, Model = model, AllowUnknown = false }
            };
        }
        public async Task<JobQueue> Queue(IModelClient client)
        {
            var runner = new GradingRunner(Library, Reader, _ => client, _ => new ProviderCredentials("synthetic-key"));
            return await JobQueue.CreateAsync(new RepositoryJobStore(Library), runner, CodeExecutor ?? Executor, Path.Combine(Root, "results", Guid.NewGuid().ToString("N")));
        }
        public async Task<Job> Run(IModelClient client, DiagnosticKind kind = DiagnosticKind.Grading)
        {
            await using var queue = await Queue(client);
            var submitted = await queue.SubmitAsync(kind, Policy, Input);
            return await Wait(queue, submitted.Id);
        }
        public async Task<Job> Run(ScriptApi api, DiagnosticKind kind = DiagnosticKind.Grading)
        {
            using var http = new HttpClient(api);
            using var client = new ProviderClient(http, Input.Provider.Protocol, Input.Provider.BaseUrl);
            return await Run(client, kind);
        }
        public async Task AssertDurableEvents(Job job)
        {
            var events = await Library.ListJobEventsAsync(job.Id);
            var snapshot = (await Library.LoadJobSnapshotsAsync()).Single(j => j.Id == job.Id);
            Check(events.Select(e => e.Sequence).SequenceEqual(Enumerable.Range(1, events.Count).Select(i => (ulong)i)) && snapshot.Sequence == job.Sequence && snapshot.State == job.State, "SQLite snapshot and ordered events agree with queue");
        }
        public ValueTask DisposeAsync() => Library.DisposeAsync();
    }

    private sealed class PixelProcessor : ILibraryMediaProcessor
    {
        public Task<PreparedMedia> PrepareAsync(ImportSource source, int maximumPages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!source.Bytes.SequenceEqual(png)) throw new InvalidDataException("Only synthetic pixel fixture allowed");
            return Task.FromResult(new PreparedMedia("image/png", 4, 3, png.ToArray()));
        }
    }
    private sealed class PixelReader : IGradingImageReader
    {
        public List<(Region? Crop, ushort Rotation)> Requests { get; } = [];
        public Task<GradingImageResult> ReadAsync(byte[] bytes, Region? crop, ushort rotation, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!bytes.SequenceEqual(png)) throw new InvalidDataException("Unexpected image bytes");
            Requests.Add((crop, rotation));
            // Pixel transport only: geometry/recognition belongs to separate document/semantic acceptance.
            return Task.FromResult(new GradingImageResult(new Image { Mime = "image/png", Base64 = Convert.ToBase64String(bytes) }, 4, 3));
        }
    }
    private sealed class RejectExecutor : IToolExecutor
    {
        public int Calls { get; private set; }
        public Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall call, string resultsDirectory, CancellationToken cancellationToken) { Calls++; throw new InvalidOperationException("Execution is outside this synthetic test"); }
    }
    private sealed class RepositoryJobStore(LibraryRepository library) : IJobStore
    {
        public async Task<IReadOnlyList<Job>> LoadJobsAsync(CancellationToken cancellationToken = default) => await library.LoadJobSnapshotsAsync(cancellationToken);
        public Task CommitAsync(Job job, JobEvent jobEvent, CancellationToken cancellationToken = default) => library.SaveJobEventAsync(job, jobEvent, cancellationToken);
    }
    private sealed class BlockingJobStore(IJobStore inner) : IJobStore
    {
        private int _blocked;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task<IReadOnlyList<Job>> LoadJobsAsync(CancellationToken cancellationToken = default) => inner.LoadJobsAsync(cancellationToken);
        public async Task CommitAsync(Job job, JobEvent jobEvent, CancellationToken cancellationToken = default)
        {
            if (job.State == JobState.Queued && Interlocked.Exchange(ref _blocked, 1) == 0)
            { Entered.TrySetResult(); await Release.Task; }
            await inner.CommitAsync(job, jobEvent, cancellationToken);
        }
    }
    private sealed class DelegateClient(Protocol protocol, Func<int, JsonArray, CancellationToken, Task<Turn>> script) : IModelClient
    {
        public Protocol Protocol => protocol;
        public int Attempts { get; private set; }
        public Task<Turn> CallAsync(ProviderCredentials credentials, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools, Action<ProviderProgress>? progress = null, CancellationToken cancellationToken = default) => script(++Attempts, history, cancellationToken);
        public Task<ModelPage> ListModelsAsync(ProviderCredentials credentials, string? cursor = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
    private sealed class ScriptApi(Protocol protocol, Func<int, JsonObject, CancellationToken, Task<List<ToolCall>>> script) : HttpMessageHandler
    {
        public int Attempts { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Check(request.Method == HttpMethod.Post && request.RequestUri!.Host == "example.invalid", "synthetic HTTP handler only");
            var body = JsonNode.Parse(await request.Content!.ReadAsStringAsync(cancellationToken)) as JsonObject ?? throw new JsonException();
            var calls = await script(++Attempts, body, cancellationToken);
            var native = Native(protocol, calls);
            string events;
            if (protocol == Protocol.Responses) events = Sse(new JsonObject { ["type"] = "response.completed", ["response"] = native });
            else
            {
                var start = native.DeepClone().AsObject();
                start["content"] = new JsonArray(); start["stop_reason"] = null;
                events = Sse(new JsonObject { ["type"] = "message_start", ["message"] = start });
                var blocks = (JsonArray)native["content"]!;
                for (var i = 0; i < blocks.Count; i++) events += Sse(new JsonObject { ["type"] = "content_block_start", ["index"] = i, ["content_block"] = blocks[i]!.DeepClone() }) + Sse(new JsonObject { ["type"] = "content_block_stop", ["index"] = i });
                events += Sse(new JsonObject { ["type"] = "message_delta", ["delta"] = new JsonObject { ["stop_reason"] = "tool_use" }, ["usage"] = native["usage"]!.DeepClone() }) + Sse(new JsonObject { ["type"] = "message_stop" });
            }
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StreamContent(new SevenByteStream(Encoding.UTF8.GetBytes(events))) };
        }
    }
    private static Turn NativeTurn(Protocol protocol, List<ToolCall> calls) => ProviderProtocol.Parse(protocol, Element(Native(protocol, calls)));
    private static JsonObject Native(Protocol protocol, List<ToolCall> calls)
    {
        var blocks = new JsonArray();
        if (protocol == Protocol.Responses) blocks.Add((JsonNode)new JsonObject { ["id"] = "synthetic-reasoning", ["type"] = "reasoning", ["summary"] = new JsonArray(), ["encrypted_content"] = "SYNTHETIC_NEVER_SEND_EXTERNALLY" });
        if (protocol == Protocol.Messages) blocks.Add((JsonNode)new JsonObject { ["type"] = "thinking", ["thinking"] = "Synthetic public summary", ["signature"] = "SYNTHETIC_NEVER_SEND_EXTERNALLY" });
        foreach (var call in calls)
        {
            JsonObject block = protocol switch
            {
                Protocol.Responses => new() { ["type"] = "function_call", ["id"] = "fc-" + call.Id, ["call_id"] = call.Id, ["name"] = call.Name, ["arguments"] = call.Arguments.GetRawText() },
                Protocol.Messages => new() { ["type"] = "tool_use", ["id"] = call.Id, ["name"] = call.Name, ["input"] = JsonNode.Parse(call.Arguments.GetRawText()) },
                _ => throw new InvalidOperationException()
            };
            blocks.Add((JsonNode)block);
        }
        if (calls.Count == 0) blocks.Add((JsonNode)(protocol switch
        {
            Protocol.Responses => new JsonObject { ["type"] = "message", ["role"] = "assistant", ["content"] = new JsonArray(new JsonObject { ["type"] = "output_text", ["text"] = "Synthetic text without a save tool" }) },
            Protocol.Messages => new JsonObject { ["type"] = "text", ["text"] = "Synthetic text without a save tool" },
            _ => throw new InvalidOperationException()
        }));
        return protocol switch
        {
            Protocol.Responses => new() { ["status"] = "completed", ["output"] = blocks, ["usage"] = new JsonObject { ["input_tokens"] = 100, ["input_tokens_details"] = new JsonObject { ["cached_tokens"] = 20, ["cache_write_tokens"] = 5 }, ["output_tokens"] = 30, ["total_tokens"] = 130 } },
            Protocol.Messages => new() { ["type"] = "message", ["role"] = "assistant", ["content"] = blocks, ["stop_reason"] = calls.Count == 0 ? "end_turn" : "tool_use", ["usage"] = new JsonObject { ["input_tokens"] = 80, ["cache_read_input_tokens"] = 15, ["cache_creation_input_tokens"] = 5, ["output_tokens"] = 30 } },
            _ => throw new InvalidOperationException()
        };
    }
    private static string Sse(JsonNode value) => "data: " + value.ToJsonString() + "\r\n\r\n";
    private sealed class SevenByteStream(byte[] bytes) : MemoryStream(bytes)
    { public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], cancellationToken); }
}
