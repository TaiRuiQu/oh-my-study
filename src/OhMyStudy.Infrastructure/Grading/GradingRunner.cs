using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Grading;

/// <summary>One paper, one model context, bounded tool rounds. Only validated tools write durable results.</summary>
public sealed class GradingRunner(LibraryRepository library, IGradingImageReader images,
    Func<Provider, IModelClient>? clientFactory = null, Func<Provider, ProviderCredentials>? credentialReader = null,
    Action<SavedQuestion>? questionSaved = null) : IJobRunner
{
    public async Task RunAsync(Job job, JobContext context)
    {
        using var trace = OperationDiagnostics.Begin(job.Id);
        if (job.Kind is not (DiagnosticKind.Grading or DiagnosticKind.References))
        {
            await library.VerifyIntegrityAsync(context.Cancellation).ConfigureAwait(false);
            return;
        }
        var input = job.Input ?? throw new ArgumentException("Whole-paper input is missing.");
        bool referenceOnly = job.Kind == DiagnosticKind.References;
        var credentials = (credentialReader ?? ReadCredentials)(input.Provider);
        var client = (clientFactory ?? (provider => new ProviderClient(provider.Protocol, provider.BaseUrl, supportsFilesApi: provider.SupportsFilesApi)))(input.Provider);
        try { await RunPaperAsync(job, context, input, client, credentials, referenceOnly).ConfigureAwait(false); }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await client.DeleteFilesAsync(credentials, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception error)
            {
                OperationDiagnostics.Record("Files.CleanupFailed", new() { ["job_id"] = job.Id, ["provider_id"] = input.Provider.Id, ["exception"] = error.ToString() }, true);
            }
            finally { if (clientFactory is null && client is IDisposable disposable) disposable.Dispose(); }
        }
    }

    private static ProviderCredentials ReadCredentials(Provider provider)
    {
        var bytes = WindowsCredentialStore.Read(provider.KeyRef ?? throw new InvalidOperationException("Set the model key first."))
            ?? throw new InvalidOperationException("Configure the model key again.");
        try { return new(new UTF8Encoding(false, true).GetString(bytes)); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }

    private async Task RunPaperAsync(Job job, JobContext context, GradingInput input, IModelClient client, ProviderCredentials credentials, bool referenceOnly)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(context.Cancellation);
        deadline.CancelAfter(TimeSpan.FromMinutes(20));
        var cancellation = deadline.Token;
        var workspace = new GradingWorkspace(job.Id, library, images);
        await context.CommitAsync(token => library.BeginGradingAsync(job, token)).ConfigureAwait(false);
        var existing = await library.GradingQuestionsAsync(input.Document.Id, cancellation).ConfigureAwait(false);
        var references = await library.GradingReferencesAsync(input.Document.Id, cancellation).ConfigureAwait(false);
        var payload = new JsonObject
        {
            ["document"] = JsonNode.Parse(DomainJson.Serialize(input.Document)),
            ["existing_questions"] = JsonNode.Parse(DomainJson.Serialize(existing.Select(saved => saved with { Question = saved.Question with { Notes = "" } }).ToList())),
            ["references"] = JsonNode.Parse(DomainJson.Serialize(references))
        };
        GradingTools.UsePageSources(payload);
        var profile = GradingModelProfile.Select(input.Model.Id);
        var history = ProviderProtocol.Initial(input.Provider.Protocol, GradingPrompts.Input(payload.ToJsonString(), profile), []);
        var instructions = GradingPrompts.Instructions(input.Document.Mode, referenceOnly, profile);
        var extra = new Dictionary<string, JsonElement>();
        var nullJson = JsonDocument.Parse("null").RootElement.Clone();
        switch (input.Provider.Protocol)
        {
            case Protocol.Responses: extra["instructions"] = DomainJson.ToElement(instructions); break;
            case Protocol.Messages: extra["system"] = DomainJson.ToElement(instructions); break;
        }
        var options = new RequestOptions
        {
            Model = new() { Id = input.Model.Id, Capabilities = input.Model.Capabilities, Raw = nullJson },
            MaxOutputTokens = input.Model.MaxTokens ?? Math.Min(input.Model.MaxOutputTokens ?? ulong.MaxValue,
                input.Thinking is BudgetThinking budget ? Math.Max(32768UL, checked(budget.Tokens + 16384))
                : input.Thinking is not null || input.Model.Reasoning == true ? 32768UL : 16384UL),
            Stream = true,
            Thinking = input.Thinking,
            AllowUnknownCapabilities = input.AllowUnknown,
            Extra = extra,
            SessionId = input.Document.Id,
            PromptCacheKey = input.Provider.Protocol == Protocol.Responses && input.Provider.CodexMode ? input.Document.Id : null
        };
        try { options.Validate(input.Provider.Protocol, true, true); }
        catch (DomainValidationException)
        {
            await context.ActivityAsync(new JobActivity
            {
                Key = "model-configuration",
                Kind = "model",
                Title = "Check model configuration",
                State = "failed",
                Detail = "This model configuration does not support the task. Check image, tool, and reasoning options. For unverified capabilities, allow trying the model before starting."
            }).ConfigureAwait(false);
            throw;
        }
        var definitions = GradingTools.Create(referenceOnly, profile);
        var viewed = new HashSet<string>(StringComparer.Ordinal);
        var saved = new HashSet<string>(StringComparer.Ordinal);
        int processedImages = 0;
        int textOnlyTurns = 0;
        for (uint ordinal = 1; ordinal <= 60; ordinal++)
        {
            cancellation.ThrowIfCancellationRequested();
            await context.ProgressAsync(Math.Min(90, ordinal), $"Pages read: {viewed.Count}. Questions saved: {saved.Count}.").ConfigureAwait(false);
            var response = await ModelActivity.CallAsync(context, ordinal,
                progress => client.CallAsync(credentials, options, history, definitions, progress, cancellation), cancellation).ConfigureAwait(false);
            var turn = response.Turn;
            decimal? estimated = response.Retries > 0 ? null : ProviderProtocol.EstimateCost(turn.Usage, input.Model);
            var usage = new JsonObject
            {
                ["model"] = options.Model.Id,
                ["provider_id"] = input.Provider.Id,
                ["usage"] = JsonNode.Parse(DomainJson.Serialize(turn.Usage)),
                ["price_input"] = input.Model.PriceInput,
                ["price_cache_read"] = input.Model.PriceCacheRead,
                ["price_cache_write"] = input.Model.PriceCacheWrite,
                ["price_output"] = input.Model.PriceOutput,
                ["currency"] = input.Model.Currency,
                ["estimated_cost"] = estimated,
                ["retries"] = response.Retries,
                ["retry_usage_unknown"] = response.Retries > 0
            };
            await context.CommitAsync(token => library.RecordModelCallAsync(job.Id, ordinal, Parse(usage), token)).ConfigureAwait(false);
            if (turn.Calls.Count == 0)
            {
                if (++textOnlyTurns >= 3)
                {
                    await context.ActivityAsync(new JobActivity
                    {
                        Key = $"model-{ordinal}",
                        Kind = "model",
                        Title = $"Recognition round {ordinal}",
                        State = "failed",
                        Detail = "The model replied without saving results. Change the model or reasoning settings and retry.",
                        Text = turn.Text.Length > 16000 ? turn.Text[..16000] : turn.Text,
                        PagesRead = viewed.Count,
                        QuestionsSaved = saved.Count
                    }).ConfigureAwait(false);
                    throw new InvalidOperationException("The model did not save results. The task is incomplete.");
                }
                if (input.Provider.Protocol == Protocol.Messages) history.Add(JsonNode.Parse(turn.Context.GetRawText()));
                else foreach (var item in turn.Context.EnumerateArray()) history.Add(JsonNode.Parse(item.GetRawText()));
                foreach (var item in ProviderProtocol.Initial(input.Provider.Protocol,
                    "Continue the current task using the available tools. Text alone does not save results. Read any remaining required pages, save the results, then call finish. Do not repeat successful operations unless a correction is needed.", []))
                    history.Add(item?.DeepClone());
                continue;
            }
            textOnlyTurns = 0;
            var feedback = new List<ToolResult>();
            bool finished = false;
            for (int index = 0; index < turn.Calls.Count; index++)
            {
                cancellation.ThrowIfCancellationRequested();
                var call = turn.Calls[index];
                var stepClock = Stopwatch.StartNew();
                string? pageId = call.Arguments.ValueKind == JsonValueKind.Object && call.Arguments.TryGetProperty("page_id", out var pageValue) && pageValue.ValueKind == JsonValueKind.String ? pageValue.GetString() : null;
                int pageIndex = input.Document.Pages.FindIndex(p => p.Id == pageId);
                var activity = new JobActivity
                {
                    Key = $"tool-{ordinal}-{index}",
                    Kind = call.Name,
                    Title = call.Name switch
                    {
                        "read_pages" => "Read pages",
                        "save_paper_info" => "Organize exam details",
                        "save_questions" => "Save questions",
                        "save_references" => "Organize reference answers",
                        "finish" => "Check exam results",
                        "python" => "Run Python",
                        "shell" => "Run PowerShell",
                        _ => "Check action"
                    },
                    PageId = pageIndex >= 0 ? pageId : null,
                    Detail = "Processing",
                    PagesRead = viewed.Count,
                    QuestionsSaved = saved.Count
                };
                await context.ActivityAsync(activity).ConfigureAwait(false);
                var attachments = new List<Image>();
                JsonObject result;
                bool error = false;
                string? exception = null;
                try
                {
                    if (finished) throw new InvalidOperationException("Tools cannot be called after finish.");
                    if (call.Arguments.ValueKind != JsonValueKind.Object)
                        throw new DomainValidationException("Tool arguments must be one JSON object that matches the tool definition.");
                    switch (call.Name)
                    {
                        case "read_pages":
                            result = await BatchAsync(call.Arguments, "pages", async page =>
                            {
                                int imageIndex = attachments.Count;
                                var metadata = await ReadPageAsync(input, page, viewed, attachments, workspace, cancellation).ConfigureAwait(false);
                                metadata["image_index"] = imageIndex;
                                return metadata;
                            }).ConfigureAwait(false); break;
                        case "save_paper_info" when !referenceOnly:
                            GradingTools.Fields(call.Arguments, "name", "subject", "earned", "possible");
                            var subject = call.Arguments.GetProperty("subject");
                            call.Arguments.TryGetProperty("earned", out var earned);
                            call.Arguments.TryGetProperty("possible", out var possible);
                            call.Arguments.TryGetProperty("name", out var name);
                            var paperInfoSaved = await context.CommitAsync(token => library.SaveGradingPaperInfoAsync(job.Id,
                                subject.ValueKind == JsonValueKind.Null ? null : subject.GetString(),
                                earned.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : earned.GetDouble(),
                                possible.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : possible.GetDouble(),
                                name.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : name.GetString(), token)).ConfigureAwait(false);
                            result = new() { ["saved"] = paperInfoSaved, ["manual_values_kept"] = !paperInfoSaved }; break;
                        case "save_questions" when !referenceOnly:
                            result = await BatchAsync(call.Arguments, "questions", async args =>
                            {
                                var previous = existing.FirstOrDefault(q => q.Question.DraftKey == args.GetProperty("draft_key").GetString())?.Question;
                                var normalized = Parse(GradingTools.WithSourceRegions(args, previous?.Sources));
                                var question = DomainJson.Deserialize<Question>(normalized.GetRawText());
                                question = FillOmittedFields(question, normalized, previous, input.Document.Mode);
                                var stored = await context.CommitAsync(token => library.SaveGradingQuestionAsync(job.Id, question, token)).ConfigureAwait(false);
                                questionSaved?.Invoke(stored);
                                saved.Add(stored.Question.DraftKey);
                                existing.RemoveAll(q => q.Question.DraftKey == stored.Question.DraftKey); existing.Add(stored);
                                var entry = new JsonObject { ["saved_id"] = stored.Id, ["draft_key"] = stored.Question.DraftKey };
                                if (processedImages > 0 && stored.Question.Stem.Concat(stored.Question.Items.SelectMany(item => item.Prompt)).OfType<ImageContent>().Any(image => image.AssetId is null))
                                    entry["image_hint"] = "This question re-crops the original page for at least one image. If a code output shows the corrected or rotated figure, set that output's asset_id on the image block.";
                                return entry;
                            }).ConfigureAwait(false); break;
                        case "save_references":
                            GradingTools.Fields(call.Arguments, "references");
                            var refs = call.Arguments.GetProperty("references").EnumerateArray().Select(answer =>
                                DomainJson.Deserialize<ReferenceAnswer>(GradingTools.WithSourceRegions(answer,
                                    references.FirstOrDefault(r => r.Key == answer.GetProperty("key").GetString())?.Sources).ToJsonString())).ToList();
                            await context.CommitAsync(token => library.SaveGradingReferencesAsync(job.Id, refs, token)).ConfigureAwait(false);
                            references = refs;
                            result = new() { ["saved"] = true }; break;
                        case "finish":
                            if (index + 1 != turn.Calls.Count) throw new InvalidOperationException("finish must be the last tool call.");
                            GradingTools.Fields(call.Arguments, "coverage");
                            var coverage = DomainJson.Deserialize<List<Coverage>>(call.Arguments.GetProperty("coverage").GetRawText());
                            await context.CommitAsync(token => library.FinishGradingAsync(job.Id, coverage, viewed, referenceOnly, saved, token)).ConfigureAwait(false);
                            result = new() { ["finished"] = true }; finished = true; break;
                        case "python" or "shell":
                            GradingTools.Fields(call.Arguments, "code", "outputs", "timeout_ms");
                            var outputPaths = GradingWorkspace.OutputPaths(call.Arguments);
                            foreach (string path in outputPaths) _ = workspace.OutputPath(path);
                            ulong timeout = 60000;
                            if (call.Arguments.TryGetProperty("timeout_ms", out var timeoutJson)
                                && (timeoutJson.ValueKind != JsonValueKind.Number || !timeoutJson.TryGetUInt64(out timeout)))
                                throw new DomainValidationException("timeout_ms must be an integer between 1 and 300000.");
                            if (timeout is 0 or > 300000) throw new DomainValidationException("timeout_ms must be between 1 and 300000.");
                            var execution = new ExecutionCall
                            {
                                ProtocolVersion = 1,
                                JobId = job.Id,
                                CallId = $"tool-{ordinal}-{index}",
                                Tool = call.Name == "python" ? ToolKind.Python : ToolKind.Shell,
                                Content = call.Arguments.GetProperty("code").GetString() ?? throw new JsonException(),
                                Arguments = [],
                                WorkingDirectory = workspace.DirectoryPath,
                                TimeoutMs = timeout
                            };
                            workspace.Prepare();
                            var executionResult = await context.ExecuteAsync(execution).ConfigureAwait(false);
                            cancellation.ThrowIfCancellationRequested();
                            result = new()
                            {
                                ["status"] = executionResult.Status,
                                ["exit_code"] = executionResult.ExitCode,
                                ["stdout"] = executionResult.Stdout,
                                ["stderr"] = executionResult.Stderr,
                                ["outputs"] = await workspace.ReadOutputsAsync(outputPaths, attachments, operation => context.CommitAsync(operation), cancellation).ConfigureAwait(false)
                            };
                            if (!executionResult.Succeeded) result["error"] = $"Code execution status: {executionResult.Status}; exit code: {executionResult.ExitCode?.ToString() ?? "unavailable"}.";
                            else if (result["outputs"]!.AsArray().Any(output => output?["error"] is not null)) result["error"] = "Some output images could not be read. Inspect outputs and correct their paths or formats.";
                            var produced = result["outputs"]?.AsArray().Count(output => output?["asset_id"] is not null) ?? 0;
                            if (produced > 0)
                            {
                                processedImages += produced;
                                result["usage_hint"] = "Set this asset_id on the matching image block of save_questions to display the processed image; keep region pointing at the original source page.";
                            }
                            break;
                        default: throw new InvalidOperationException("This tool is not allowed for the current task.");
                    }
                    error = result.ContainsKey("error");
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception caught) when (caught is DomainValidationException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException
                    || (call.Name is "python" or "shell") && (caught is IOException or System.ComponentModel.Win32Exception))
                {
                    exception = caught.ToString();
                    bool executionFailure = (call.Name is "python" or "shell") && (caught is IOException or System.ComponentModel.Win32Exception);
                    result = new() { ["error"] = caught is DomainValidationException || executionFailure ? caught.Message : "Tool arguments or business constraints are invalid. Correct them according to the definitions." };
                    if (executionFailure)
                    {
                        result["status"] = "failed";
                        result["stderr"] = caught.Message;
                    }
                    error = true;
                }
                string detail = error ? result["error"]!.GetValue<string>() : call.Name switch
                {
                    "read_pages" => "Original pages read",
                    "save_paper_info" => result["saved"]!.GetValue<bool>() ? "Exam details saved" : "Manual exam details preserved",
                    "save_questions" => "Questions added to question bank",
                    "save_references" => "Reference answers saved",
                    "finish" => "Page coverage and saved results verified",
                    "python" or "shell" => "Code execution completed",
                    _ => "Action completed"
                };
                string? stderr = result["stderr"]?.GetValue<string>();
                string stepText = call.Name is "python" or "shell" ? string.Join('\n', new[] { result["stdout"]?.GetValue<string>(), !string.IsNullOrWhiteSpace(stderr) ? stderr : error ? detail : null }.Where(value => !string.IsNullOrEmpty(value))) : "";
                await context.ActivityAsync(activity with
                {
                    State = error ? "failed" : "completed",
                    Detail = detail.Length > 1000 ? detail[..1000] : detail,
                    Text = stepText.Length > 16000 ? stepText[..16000] : stepText,
                    PagesRead = viewed.Count,
                    QuestionsSaved = saved.Count,
                    ElapsedMs = stepClock.ElapsedMilliseconds
                }).ConfigureAwait(false);
                await context.ProgressAsync(Math.Min(90, ordinal), $"Pages read: {viewed.Count}. Questions saved: {saved.Count}.").ConfigureAwait(false);
                var diagnostic = new JsonObject { ["job_id"] = job.Id, ["round"] = ordinal, ["index"] = index, ["call_id"] = call.Id, ["tool"] = call.Name, ["elapsed_ms"] = stepClock.ElapsedMilliseconds, ["attachments"] = attachments.Count };
                if (error)
                {
                    diagnostic["arguments"] = JsonNode.Parse(call.Arguments.GetRawText());
                    diagnostic["result"] = result.DeepClone();
                    diagnostic["exception"] = exception;
                }
                else if (stepText.Length > 0) diagnostic["output"] = stepText;
                OperationDiagnostics.Record(error ? "Tool.Failed" : "Tool.Completed", diagnostic, error);
                feedback.Add(new() { Call = call, Text = result.ToJsonString(), Images = attachments, IsError = error });
            }
            if (finished) return;
            ProviderProtocol.ContinueWith(input.Provider.Protocol, history, turn, feedback);
        }
        throw new InvalidOperationException("The task reached the call limit. Saved results will be kept.");
    }

    private static async Task<JsonObject> BatchAsync(JsonElement arguments, string name, Func<JsonElement, Task<JsonObject>> execute)
    {
        GradingTools.Fields(arguments, name);
        var entries = arguments.GetProperty(name);
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() == 0)
            throw new JsonException("A batch must contain at least one entry.");
        var results = new JsonArray();
        int failures = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            JsonObject result;
            try { result = await execute(entry).ConfigureAwait(false); }
            catch (Exception error) when (error is DomainValidationException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
            {
                OperationDiagnostics.Record("Tool.EntryFailed", new() { ["exception"] = error.ToString(), ["entry"] = JsonNode.Parse(entry.GetRawText()) }, true);
                result = new() { ["error"] = error is DomainValidationException or JsonException ? error.Message : "Invalid entry. Correct it according to the tool definition." };
                failures++;
            }
            result["index"] = results.Count;
            results.Add((JsonNode)result);
        }
        var batch = new JsonObject { [name] = results };
        if (failures != 0) batch["error"] = $"{failures} entries failed. Successful entries were retained; retry only the failed entries.";
        return batch;
    }

    private async Task<JsonObject> ReadPageAsync(GradingInput input, JsonElement arguments, HashSet<string> viewed, List<Image> attachments, GradingWorkspace workspace, CancellationToken cancellationToken)
    {
        GradingTools.Fields(arguments, "page_id", "crop", "rotation");
        var id = arguments.GetProperty("page_id").GetString();
        var page = input.Document.Pages.FirstOrDefault(p => p.Id == id) ?? throw new DomainValidationException("The source page does not belong to this document.");
        if (page.PreviewStatus != "ready") throw new DomainValidationException("The page has not been read.");
        Region? crop = arguments.TryGetProperty("crop", out var cropJson) && cropJson.ValueKind != JsonValueKind.Null ? GradingCoordinates.FromModel(cropJson) : null;
        if (crop is not null)
        {
            if (crop.PageId != page.Id) throw new DomainValidationException("The crop does not match the source page.");
            GradingValidation.ValidateRegion(crop, input.Document, page.Role);
        }
        ushort rotation = page.Rotation;
        if (arguments.TryGetProperty("rotation", out var rotationJson) && rotationJson.ValueKind != JsonValueKind.Null)
        {
            if (rotationJson.ValueKind != JsonValueKind.Number || !rotationJson.TryGetUInt16(out rotation)) throw new DomainValidationException("Rotation must be 0, 90, 180, or 270 degrees.");
        }
        if (rotation is not (0 or 90 or 180 or 270)) throw new DomainValidationException("Rotation must be 0, 90, 180, or 270 degrees.");
        var asset = await library.ReadAssetAsync(page.AssetId, cancellationToken: cancellationToken).ConfigureAwait(false);
        var image = await images.ReadAsync(asset.Bytes, crop, rotation, cancellationToken).ConfigureAwait(false);
        attachments.Add(image.Image);
        if (crop is null) viewed.Add(page.Id);
        return new()
        {
            ["page_id"] = page.Id,
            ["rotation"] = rotation,
            ["crop"] = crop is null ? null : GradingCoordinates.ToModelRegion(crop),
            ["width"] = image.Width,
            ["height"] = image.Height,
            ["coordinate_system"] = "unrotated_source_discrete_0_999",
            ["source_file"] = await workspace.WriteImageAsync(asset.Bytes, cancellationToken, asset.Mime).ConfigureAwait(false),
            ["source_width"] = page.Width,
            ["source_height"] = page.Height,
            ["local_file"] = await workspace.WriteImageAsync(Convert.FromBase64String(image.Image.Base64), cancellationToken, image.Image.Mime).ConfigureAwait(false)
        };
    }

    // Omission means "no new information" on re-recognition; explicit empty values remain editable.
    private static Question FillOmittedFields(Question q, JsonElement args, Question? old, ImportMode mode)
    {
        bool Has(string name) => args.TryGetProperty(name, out _);
        var rawItems = args.GetProperty("items").EnumerateArray().ToList();
        var items = q.Items.Select((item, index) => FillOmittedFields(item, rawItems[index], old?.Items.FirstOrDefault(o => o.Key == item.Key), mode)).ToList();
        return q with
        {
            Subject = Has("subject") ? q.Subject : old?.Subject ?? "",
            QuestionType = Has("question_type") ? q.QuestionType : old?.QuestionType ?? "",
            KnowledgePoints = Has("knowledge_points") ? q.KnowledgePoints : old?.KnowledgePoints ?? [],
            Uncertainties = Has("uncertainties") ? q.Uncertainties : old?.Uncertainties ?? [],
            Items = items
        };
    }

    private static QuestionItem FillOmittedFields(QuestionItem item, JsonElement args, QuestionItem? old, ImportMode mode)
    {
        bool Has(string name) => args.TryGetProperty(name, out _);
        Attempt? attempt = item.Attempt;
        if (!Has("attempt")) attempt = old?.Attempt ?? (mode is ImportMode.Blank or ImportMode.Auto ? null : new Attempt());
        else if (attempt is not null && args.GetProperty("attempt") is { ValueKind: JsonValueKind.Object } raw)
        {
            var previous = old?.Attempt;
            attempt = attempt with
            {
                StudentAnswer = raw.TryGetProperty("student_answer", out _) ? attempt.StudentAnswer : previous?.StudentAnswer ?? "",
                StudentRegions = raw.TryGetProperty("student_regions", out _) ? attempt.StudentRegions : previous?.StudentRegions ?? [],
                Teacher = raw.TryGetProperty("teacher", out var teacher)
                    ? attempt.Teacher is { } mark && teacher.ValueKind == JsonValueKind.Object && !teacher.TryGetProperty("regions", out _)
                        ? mark with { Regions = previous?.Teacher?.Regions ?? [] } : attempt.Teacher
                    : previous?.Teacher,
                Grade = raw.TryGetProperty("grade", out _) ? attempt.Grade : previous?.Grade ?? attempt.Grade,
                ReferenceKey = raw.TryGetProperty("reference_key", out _) ? attempt.ReferenceKey : previous?.ReferenceKey,
                AiSolution = raw.TryGetProperty("ai_solution", out _) ? attempt.AiSolution : previous?.AiSolution ?? "",
                ErrorAnalysis = raw.TryGetProperty("error_analysis", out _) ? attempt.ErrorAnalysis
                    : previous?.StudentAnswer == attempt.StudentAnswer && previous?.Grade.Verdict == attempt.Grade.Verdict ? previous.ErrorAnalysis : null,
                Conflict = raw.TryGetProperty("conflict", out _) ? attempt.Conflict : previous?.Conflict
            };
        }
        if (attempt is not null && attempt.Grade.Verdict is not (Verdict.Partial or Verdict.Incorrect))
            attempt = attempt with { ErrorAnalysis = null };
        return item with
        {
            Label = Has("label") ? item.Label : old?.Label ?? "",
            Prompt = Has("prompt") ? item.Prompt : old?.Prompt ?? [],
            Options = Has("options") ? item.Options : old?.Options ?? [],
            Points = Has("points") ? item.Points : old?.Points,
            Answer = Has("answer") ? item.Answer : old?.Answer ?? "",
            Explanation = Has("explanation") ? item.Explanation : old?.Explanation ?? "",
            Attempt = attempt
        };
    }

    private static JsonElement Parse(JsonNode value) { using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone(); }
}
