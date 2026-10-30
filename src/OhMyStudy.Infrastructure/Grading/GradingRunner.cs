using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Grading;

public sealed partial class GradingRunner(LibraryRepository library, IGradingImageReader images,
    Func<Provider, IModelClient>? clientFactory = null, Func<Provider, ProviderCredentials>? credentialReader = null,
    Action<SavedQuestion>? questionSaved = null, IQuestionRegionDetector? regionDetector = null) : IJobRunner
{
    private readonly SemaphoreSlim pythonGate = new(1, 1);
    private int ordinal;

    public async Task RunAsync(Job job, JobContext context)
    {
        using var trace = OperationDiagnostics.Begin(job.Id);
        if (job.Kind is not (DiagnosticKind.Grading or DiagnosticKind.References))
        {
            await library.VerifyIntegrityAsync(context.Cancellation).ConfigureAwait(false);
            return;
        }
        var input = job.Input ?? throw new ArgumentException("Exam input is missing.");
        var document = input.Document;
        var settings = input.Workflow ?? (await library.GetSettingsAsync(context.Cancellation).ConfigureAwait(false)).ExamWorkflow
            ?? throw new InvalidOperationException("Configure the exam workflow in Settings, including MinerU and the question splitting model.");
        ConfigurationValidation.ValidateWorkflow(settings);
        var ocrToken = ReadSecret(settings.MinerUTokenRef, "Configure the MinerU Bearer token in Settings.");
        var splitterModel = input.Splitter ?? throw new InvalidOperationException("Select the question splitting model from a provider in Settings.");
        _ = (credentialReader ?? ReadCredentials)(splitterModel.Provider);
        bool deepSeekSplitter = new Uri(splitterModel.Provider.BaseUrl).Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        if (!deepSeekSplitter)
            Options(splitterModel.Provider, splitterModel.Model, null, input.AllowUnknown, WorkflowPrompts.Split)
                .Validate(splitterModel.Provider.Protocol, true, false);
        if (job.Kind != DiagnosticKind.References)
        {
            _ = (credentialReader ?? ReadCredentials)(input.Provider);
            Options(input.Provider, input.Model, input.Thinking, input.AllowUnknown, WorkflowPrompts.Solve).Validate(input.Provider.Protocol, true, true);
            if (document.Mode != ImportMode.Blank)
            {
                if (input.Grader is { } grader) _ = (credentialReader ?? ReadCredentials)(grader.Provider);
                var assessmentModel = input.Grader ?? new WorkflowModel(input.Provider, input.Model);
                Options(assessmentModel.Provider, assessmentModel.Model, input.Grader is null ? input.Thinking : null, input.AllowUnknown, WorkflowPrompts.Assess)
                    .Validate(assessmentModel.Provider.Protocol, true, false);
            }
        }
        foreach (var path in new[] { settings.RegionModelPath, settings.ChineseRegionModelPath }.OfType<string>())
            if (!File.Exists(path)) throw new FileNotFoundException("Question region ONNX model not found.", path);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(30));
        var cancellation = deadline.Token;
        bool referencesOnly = job.Kind == DiagnosticKind.References;
        if (document.Pages.Count == 0 || document.Pages.Any(p => p.PreviewStatus != "ready")) throw new InvalidOperationException("Render the required source pages before recognition.");
        ordinal = 0;
        await context.CommitAsync(ct => library.BeginGradingAsync(job, ct)).ConfigureAwait(false);
        ExamLayout? layout = null;
        if (!referencesOnly)
        {
            layout = await ClassifyLayoutAsync(context, input, splitterModel, settings, cancellation).ConfigureAwait(false);
            document = layout.Apply(document);
        }
        var pages = document.Pages.Where(p => !referencesOnly || p.Role == "reference").ToList();
        var existing = await library.GradingQuestionsAsync(document.Id, cancellation).ConfigureAwait(false);
        var previousReferences = await library.GradingReferencesAsync(document.Id, cancellation).ConfigureAwait(false);
        using var mineru = new MinerUClient(library, images);
        var ocrTask = mineru.RecognizeAsync(context, pages, ocrToken, cancellation);
        var detectionTask = DetectAsync(context, pages, settings, cancellation);
        await Task.WhenAll(ocrTask, detectionTask).ConfigureAwait(false);
        var ocr = await ocrTask.ConfigureAwait(false);
        var detections = await detectionTask.ConfigureAwait(false);
        var pageImages = new List<Image>();
        foreach (var page in pages)
        {
            var asset = await library.ReadAssetAsync(page.AssetId, cancellationToken: cancellation).ConfigureAwait(false);
            pageImages.Add((await images.ReadAsync(asset.Bytes, null, 0, cancellation).ConfigureAwait(false)).Image);
        }
        var payload = new JsonObject
        {
            ["mode"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(document.Mode.ToString()), ["reference_only"] = referencesOnly,
            ["allowed_answer_binding_statuses"] = document.Mode switch
            {
                ImportMode.Blank => new JsonArray("none"),
                ImportMode.Auto => new JsonArray("matched", "blank", "unmatched", "none"),
                _ => new JsonArray("matched", "blank", "unmatched")
            },
            ["exam_layout"] = layout is null ? null : DomainJson.ToElement(layout).AsNode(),
            ["page_order"] = DomainJson.ToElement(pages).AsNode(), ["ocr"] = DomainJson.ToElement(ocr).AsNode(),
            ["detections"] = DomainJson.ToElement(detections).AsNode(),
            ["existing_questions"] = DomainJson.ToElement(existing.Select(q => q with { Question = q.Question with { Notes = "" } }).ToList()).AsNode(),
            ["existing_references"] = DomainJson.ToElement(previousReferences).AsNode()
        };
        await context.ActivityAsync(new JobActivity { Key = "split", Kind = "split", Title = "Group questions and materials",
            Detail = splitterModel.Provider.Name + " / " + splitterModel.Model.Label }).ConfigureAwait(false);
        QuestionSplitPlan plan;
        QuestionSplitPlan? previousPlan = null;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (deepSeekSplitter)
                {
                    using var splitter = new QuestionSplitter(library);
                    plan = await splitter.SplitAsync(context, settings, splitterModel, (credentialReader ?? ReadCredentials)(splitterModel.Provider).Key, checked((uint)Interlocked.Increment(ref ordinal)),
                        $"split-plan-{attempt}.json", payload, pageImages, cancellation).ConfigureAwait(false);
                }
                else
                {
                    plan = await CallAsync<QuestionSplitPlan>(context, splitterModel.Provider, splitterModel.Model, null, input.AllowUnknown,
                        "split", WorkflowPrompts.Split, Parse(payload), pageImages, settings, new GradingWorkspace(job.Id), cancellation).ConfigureAwait(false);
                }
                previousPlan = plan;
                plan = ResolveAnswerRegions(plan, ocr, document);
                await context.CommitAsync(ct => library.SaveWorkflowArtifactAsync(job.Id, "split-plan.json", "application/json", Encoding.UTF8.GetBytes(DomainJson.Serialize(plan)), ct)).ConfigureAwait(false);
                ValidatePlan(plan, ocr, detections, document, existing, referencesOnly, layout);
                await context.CommitAsync(ct => library.SaveGradingReferencesAsync(job.Id, plan.References, ct)).ConfigureAwait(false);
                break;
            }
            catch (Exception error) when (attempt < 3 && error is DomainValidationException or JsonException)
            {
                OperationDiagnostics.Record("Workflow.SplitCorrection", new() { ["attempt"] = attempt, ["next_attempt"] = attempt + 1,
                    ["exception"] = error.ToString(), ["response"] = previousPlan is null ? null : DomainJson.ToElement(previousPlan).AsNode() }, true);
                if (previousPlan is not null) payload["previous_plan"] = DomainJson.ToElement(previousPlan).AsNode();
                payload["validation_error"] = error.Message;
                await context.ActivityAsync(new JobActivity { Key = "split", Kind = "split", Title = "Group questions and materials",
                    Detail = "Correcting question grouping after validation." }).ConfigureAwait(false);
            }
        }
        await context.ActivityAsync(new JobActivity { Key = "split", Kind = "split", Title = "Group questions and materials", State = "completed",
            Detail = $"Questions grouped: {plan.Questions.Count}.", QuestionsSaved = plan.Questions.Count }).ConfigureAwait(false);
        if (referencesOnly)
        {
            await context.CommitAsync(ct => library.FinishGradingAsync(job.Id, plan.Coverage, pages.Select(p => p.Id).ToHashSet(), true, new HashSet<string>(), ct)).ConfigureAwait(false);
            return;
        }
        await context.CommitAsync(ct => library.SaveGradingPaperInfoAsync(job.Id, plan.Subject, null, null, plan.Name, ct)).ConfigureAwait(false);
        var drafts = plan.Questions.Select(split => CreateDraft(split, ocr, detections, document.Mode, plan.References,
            existing.FirstOrDefault(q => q.Question.DraftKey == split.Key)?.Question)).ToList();
        if (layout is not null)
            drafts = drafts.Select(draft => draft with { Uncertainties = draft.Uncertainties.Concat(layout.Uncertainties)
                .Concat(layout.Confidence < .8 || layout.Format == ExamFormat.Uncertain ? new[] { "Exam format needs review." } : []).Distinct().ToList() }).ToList();
        foreach (var draft in drafts)
        {
            await context.CommitAsync(ct => library.SaveGradingQuestionAsync(job.Id, draft, ct)).ConfigureAwait(false);
            await StageAsync(context, draft, "question", "Question", "completed").ConfigureAwait(false);
        }
        var savedDrafts = await library.GradingQuestionsAsync(document.Id, cancellation).ConfigureAwait(false);
        GradingValidation.ValidateCoverage(document, plan.Coverage, savedDrafts, false, pages.Select(p => p.Id).ToHashSet());
        await context.ActivityAsync(new JobActivity { Key = "drafts", Kind = "split", Title = "Group questions and materials", State = "completed",
            Detail = $"Questions grouped: {drafts.Count}.", PagesRead = pages.Count, QuestionsSaved = drafts.Count }).ConfigureAwait(false);
        await context.ProgressAsync(35, $"Questions grouped: {drafts.Count}. Solving independently.").ConfigureAwait(false);
        int completed = 0;
        var workspace = new GradingWorkspace(job.Id);
        workspace.Prepare();
        await Parallel.ForEachAsync(drafts, new ParallelOptions { MaxDegreeOfParallelism = settings.ParallelQuestions, CancellationToken = cancellation }, async (draft, ct) =>
        {
            try
            {
                var split = plan.Questions.Single(q => q.Key == draft.DraftKey);
                await StageAsync(context, draft, "solve", "Solve question", "running").ConfigureAwait(false);
                var evidence = await EvidenceAsync(document, draft.Sources, ct).ConfigureAwait(false);
                var solveQuestion = DomainJson.ToElement(draft).AsNode()!.AsObject();
                foreach (var item in solveQuestion["items"]!.AsArray())
                {
                    item!.AsObject().Remove("attempt");
                    item.AsObject().Remove("answer");
                    item.AsObject().Remove("explanation");
                }
                var solvePayload = new JsonObject { ["question"] = solveQuestion,
                    ["image_regions"] = DomainJson.ToElement(evidence.Regions).AsNode(), ["image_rotations"] = ImageRotations(document, evidence.Regions) };
                var solution = await CallAsync<QuestionSolution>(context, input.Provider, input.Model, input.Thinking, input.AllowUnknown,
                    "solve", WorkflowPrompts.Solve, Parse(solvePayload), evidence.Images, settings, workspace, ct,
                    result => RequireItems(draft, result.Items.Select(i => i.Key))).ConfigureAwait(false);
                var solved = draft with
                {
                    Items = draft.Items.Select(item =>
                    {
                        var answer = solution.Items.Single(s => s.Key == item.Key);
                        return item with { Answer = answer.Answer, Explanation = answer.Explanation };
                    }).ToList(),
                    KnowledgePoints = solution.KnowledgePoints, Uncertainties = solution.Uncertainties.Concat(draft.Uncertainties.Where(u => u != "Awaiting independent solution.")).Distinct().ToList()
                };
                await StageAsync(context, draft, "solve", "Solve question", "completed").ConfigureAwait(false);
                bool assess = document.Mode is ImportMode.AiGrading or ImportMode.TeacherGraded
                    || document.Mode == ImportMode.Auto && (split.HasStudentWork || split.StudentRegions.Count > 0 || split.AnswerBindings.Any(b => b.Status != "none") || draft.Items.Any(i => i.Attempt is not null));
                if (assess)
                {
                    ct.ThrowIfCancellationRequested();
                    await context.CommitAsync(token => library.SaveGradingQuestionAsync(job.Id, solved, token)).ConfigureAwait(false);
                    solved = await AssessQuestionAsync(context, input, document, layout, split, solved, plan.References, settings, workspace, ct).ConfigureAwait(false);
                }
                else await StageAsync(context, draft, "assess", "Assess student work", "completed", "Grading not required.").ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var stored = await context.CommitAsync(token => library.SaveGradingQuestionAsync(job.Id, solved, token)).ConfigureAwait(false);
                questionSaved?.Invoke(stored);
                int done = Interlocked.Increment(ref completed);
                await context.ProgressAsync(35 + 60d * done / Math.Max(1, drafts.Count), $"Questions completed: {done}/{drafts.Count}.").ConfigureAwait(false);
            }
            catch (Exception error)
            {
                OperationDiagnostics.Record("Workflow.QuestionFailed", new() { ["question_key"] = draft.DraftKey, ["number"] = draft.Number, ["exception"] = error.ToString() }, true);
                await StageAsync(context, draft, "result", "Process question", "failed", error.Message).ConfigureAwait(false);
                throw;
            }
        }).ConfigureAwait(false);
        await context.CommitAsync(ct => library.FinishGradingAsync(job.Id, plan.Coverage, pages.Select(p => p.Id).ToHashSet(), false,
            drafts.Select(q => q.DraftKey).ToHashSet(), ct)).ConfigureAwait(false);
        await context.ProgressAsync(100, "OCR, question grouping, solving, and assessment completed.").ConfigureAwait(false);
    }

    private async Task<List<DetectedQuestionRegion>> DetectAsync(JobContext context, List<SourcePage> pages, ExamWorkflowSettings settings, CancellationToken cancellationToken)
    {
        var result = new List<DetectedQuestionRegion>();
        string bundled = Path.Combine(AppContext.BaseDirectory, "native", "question-regions", "base.onnx");
        var paths = new[] { settings.RegionModelPath ?? (File.Exists(bundled) ? bundled : null), settings.ChineseRegionModelPath }.OfType<string>().Distinct().ToList();
        if (paths.Count > 0 && regionDetector is null) throw new InvalidOperationException("The question region detector is unavailable.");
        foreach (var page in pages.Where(p => p.Role is "material" or "student"))
        {
            var asset = await library.ReadAssetAsync(page.AssetId, cancellationToken: cancellationToken).ConfigureAwait(false);
            foreach (var path in paths)
                result.AddRange(await regionDetector!.DetectAsync(page, asset.Bytes, path, cancellationToken).ConfigureAwait(false));
        }
        await context.ActivityAsync(new JobActivity { Key = "regions", Kind = "regions", Title = "Detect question regions", State = "completed",
            Detail = paths.Count == 0 ? "No region model configured. OCR and original source images will be reviewed by the splitting model."
                : $"Detected regions: {result.Count}; models: {paths.Count}." }).ConfigureAwait(false);
        await context.CommitAsync(ct => library.SaveWorkflowArtifactAsync(context.JobId, "regions.json", "application/json", Encoding.UTF8.GetBytes(DomainJson.Serialize(result)), ct)).ConfigureAwait(false);
        return result;
    }

    private static JsonArray ImageRotations(Document document, List<Region> regions)
        => new(regions.Select(r => (JsonNode?)JsonValue.Create(document.Pages.Single(p => p.Id == r.PageId).Rotation)).ToArray());
}
