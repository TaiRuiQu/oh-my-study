using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Grading;

/// <summary>One paper, one model context, bounded tool rounds. Only validated tools write durable results.</summary>
public sealed class GradingRunner(LibraryRepository library, IGradingImageReader images,
    Func<Provider, IModelClient>? clientFactory = null, Func<Provider, ProviderCredentials>? credentialReader = null) : IJobRunner
{
    public async Task RunAsync(Job job, JobContext context)
    {
        if (job.Kind is not (DiagnosticKind.Grading or DiagnosticKind.References))
        {
            await library.VerifyIntegrityAsync(context.Cancellation).ConfigureAwait(false);
            return;
        }
        var input = job.Input ?? throw new ArgumentException("Whole-paper input is missing.");
        bool referenceOnly = job.Kind == DiagnosticKind.References;
        var credentials = (credentialReader ?? ReadCredentials)(input.Provider);
        var client = (clientFactory ?? (provider => new ProviderClient(provider.Protocol, provider.BaseUrl)))(input.Provider);
        try { await RunPaperAsync(job, context, input, client, credentials, referenceOnly).ConfigureAwait(false); }
        finally { if (clientFactory is null && client is IDisposable disposable) disposable.Dispose(); }
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
        await context.CommitAsync(token => library.BeginGradingAsync(job, token)).ConfigureAwait(false);
        var existing = await library.GradingQuestionsAsync(input.Document.Id, cancellation).ConfigureAwait(false);
        var references = await library.GradingReferencesAsync(input.Document.Id, cancellation).ConfigureAwait(false);
        var payload = new JsonObject
        {
            ["document"] = JsonNode.Parse(DomainJson.Serialize(input.Document)),
            ["existing_questions"] = JsonNode.Parse(DomainJson.Serialize(existing.Select(saved => saved with { Question = saved.Question with { Notes = "" } }).ToList())),
            ["references"] = JsonNode.Parse(DomainJson.Serialize(references))
        };
        var history = ProviderProtocol.Initial(input.Provider.Protocol, GradingPrompts.Input(payload.ToJsonString()), []);
        var instructions = GradingPrompts.Instructions(input.Document.Mode, referenceOnly);
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
            MaxOutputTokens = Math.Min(input.Model.MaxOutputTokens ?? ulong.MaxValue,
                input.Thinking is BudgetThinking budget ? Math.Max(32768UL, checked(budget.Tokens + 16384))
                : input.Thinking is not null || input.Model.Reasoning == true ? 32768UL : 16384UL),
            Stream = true,
            Thinking = input.Thinking,
            AllowUnknownCapabilities = input.AllowUnknown,
            Extra = extra
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
        var definitions = GradingTools.Create(referenceOnly);
        var viewed = new HashSet<string>(StringComparer.Ordinal);
        var saved = new HashSet<string>(StringComparer.Ordinal);
        int textOnlyTurns = 0;
        for (uint ordinal = 1; ordinal <= 60; ordinal++)
        {
            cancellation.ThrowIfCancellationRequested();
            await context.ProgressAsync(Math.Min(90, ordinal), $"Pages read: {viewed.Count}. Questions saved: {saved.Count}.").ConfigureAwait(false);
            var turn = await ModelActivity.CallAsync(context, ordinal,
                progress => client.CallAsync(credentials, options, history, definitions, progress, cancellation)).ConfigureAwait(false);
            decimal? estimated = ProviderProtocol.EstimateCost(turn.Usage, input.Model);
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
                ["retries"] = 0
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
                        "read_page" => pageIndex >= 0 ? $"Read page {pageIndex + 1}" : "Read pages",
                        "save_paper_info" => "Organize exam details",
                        "save_question" => "Save questions",
                        "save_references" => "Organize reference answers",
                        "finish" => "Check exam results",
                        "python" or "shell" => "Run calculations",
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
                try
                {
                    if (finished) throw new InvalidOperationException("Tools cannot be called after finish.");
                    switch (call.Name)
                    {
                        case "read_page":
                            result = await ReadPageAsync(input, call.Arguments, viewed, attachments, cancellation).ConfigureAwait(false); break;
                        case "save_paper_info" when !referenceOnly:
                            GradingTools.Fields(call.Arguments, "subject", "earned", "possible");
                            var subject = call.Arguments.GetProperty("subject");
                            call.Arguments.TryGetProperty("earned", out var earned);
                            call.Arguments.TryGetProperty("possible", out var possible);
                            var paperInfoSaved = await context.CommitAsync(token => library.SaveGradingPaperInfoAsync(job.Id,
                                subject.ValueKind == JsonValueKind.Null ? null : subject.GetString(),
                                earned.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : earned.GetDouble(),
                                possible.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined ? null : possible.GetDouble(), token)).ConfigureAwait(false);
                            result = new() { ["saved"] = paperInfoSaved, ["manual_values_kept"] = !paperInfoSaved }; break;
                        case "save_question" when !referenceOnly:
                            var question = DomainJson.Deserialize<Question>(call.Arguments.GetRawText());
                            question = FillOmittedFields(question, call.Arguments, existing.FirstOrDefault(q => q.Question.DraftKey == question.DraftKey)?.Question, input.Document.Mode);
                            var stored = await context.CommitAsync(token => library.SaveGradingQuestionAsync(job.Id, question, token)).ConfigureAwait(false);
                            saved.Add(stored.Question.DraftKey);
                            existing.RemoveAll(q => q.Question.DraftKey == stored.Question.DraftKey); existing.Add(stored);
                            result = new() { ["saved_id"] = stored.Id, ["draft_key"] = stored.Question.DraftKey }; break;
                        case "save_references":
                            GradingTools.Fields(call.Arguments, "references");
                            var refs = DomainJson.Deserialize<List<ReferenceAnswer>>(call.Arguments.GetProperty("references").GetRawText());
                            await context.CommitAsync(token => library.SaveGradingReferencesAsync(job.Id, refs, token)).ConfigureAwait(false);
                            result = new() { ["saved"] = true }; break;
                        case "finish":
                            if (index + 1 != turn.Calls.Count) throw new InvalidOperationException("finish must be the last tool call.");
                            GradingTools.Fields(call.Arguments, "coverage");
                            var coverage = DomainJson.Deserialize<List<Coverage>>(call.Arguments.GetProperty("coverage").GetRawText());
                            await context.CommitAsync(token => library.FinishGradingAsync(job.Id, coverage, viewed, referenceOnly, saved, token)).ConfigureAwait(false);
                            result = new() { ["finished"] = true }; finished = true; break;
                        case "python" or "shell" when !referenceOnly:
                            GradingTools.Fields(call.Arguments, "code");
                            var execution = new ExecutionCall
                            {
                                ProtocolVersion = 1,
                                JobId = job.Id,
                                CallId = $"tool-{ordinal}-{index}",
                                Tool = call.Name == "python" ? ToolKind.Python : ToolKind.Shell,
                                Content = call.Arguments.GetProperty("code").GetString() ?? throw new JsonException(),
                                Arguments = [],
                                WorkingDirectory = Path.Combine(Path.GetTempPath(), "OhMyStudy", "tasks", job.Id),
                                TimeoutMs = 60000
                            };
                            // The directory is application-owned; it is never taken from model arguments.
                            RuntimePathsForTask(execution.WorkingDirectory);
                            var executionResult = await context.ExecuteAsync(execution).ConfigureAwait(false);
                            if (!executionResult.Succeeded) throw new InvalidOperationException("Code execution did not complete.");
                            result = new() { ["stdout"] = executionResult.Stdout }; break;
                        default: throw new InvalidOperationException("This tool is not allowed for the current task.");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception caught) when (caught is DomainValidationException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                {
                    // Tool validation is useful feedback to the model; transport, credentials and IO failures do not loop.
                    result = new() { ["error"] = caught is DomainValidationException ? caught.Message : "Tool arguments or business constraints are invalid. Correct them according to the definitions." };
                    error = true;
                }
                string detail = error ? result["error"]!.GetValue<string>() : call.Name switch
                {
                    "read_page" => call.Arguments.TryGetProperty("crop", out var cropValue) && cropValue.ValueKind == JsonValueKind.Object ? "Page region viewed" : "Original read",
                    "save_paper_info" => result["saved"]!.GetValue<bool>() ? "Exam details saved" : "Manual exam details preserved",
                    "save_question" => "Question added to question bank",
                    "save_references" => "Reference answers saved",
                    "finish" => "Page coverage and saved results verified",
                    "python" or "shell" => "Calculation completed",
                    _ => "Action completed"
                };
                string stepText = call.Name is "python" or "shell" && !error ? result["stdout"]?.GetValue<string>() ?? "" : "";
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
                feedback.Add(new() { Call = call, Text = result.ToJsonString(), Images = attachments, IsError = error });
            }
            if (finished) return;
            ProviderProtocol.ContinueWith(input.Provider.Protocol, history, turn, feedback);
        }
        throw new InvalidOperationException("The task reached the call limit. Saved results will be kept.");
    }

    private async Task<JsonObject> ReadPageAsync(GradingInput input, JsonElement arguments, HashSet<string> viewed, List<Image> attachments, CancellationToken cancellationToken)
    {
        GradingTools.Fields(arguments, "page_id", "crop", "rotation");
        var id = arguments.GetProperty("page_id").GetString();
        var page = input.Document.Pages.FirstOrDefault(p => p.Id == id) ?? throw new DomainValidationException("The source page does not belong to this document.");
        if (page.PreviewStatus != "ready") throw new DomainValidationException("The page has not been read.");
        Region? crop = arguments.TryGetProperty("crop", out var cropJson) && cropJson.ValueKind != JsonValueKind.Null ? DomainJson.Deserialize<Region>(cropJson.GetRawText()) : null;
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
            ["crop"] = crop is null ? null : JsonNode.Parse(DomainJson.Serialize(crop)),
            ["width"] = image.Width,
            ["height"] = image.Height,
            ["coordinate_system"] = "unrotated_source_normalized"
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
        if (!Has("attempt")) attempt = old?.Attempt ?? (mode == ImportMode.Blank ? null : new Attempt());
        else if (attempt is not null && args.GetProperty("attempt") is { ValueKind: JsonValueKind.Object } raw)
        {
            var previous = old?.Attempt;
            attempt = attempt with
            {
                StudentAnswer = raw.TryGetProperty("student_answer", out _) ? attempt.StudentAnswer : previous?.StudentAnswer ?? "",
                StudentRegions = raw.TryGetProperty("student_regions", out _) ? attempt.StudentRegions : previous?.StudentRegions ?? [],
                Teacher = raw.TryGetProperty("teacher", out _) ? attempt.Teacher : previous?.Teacher,
                Grade = raw.TryGetProperty("grade", out _) ? attempt.Grade : previous?.Grade ?? attempt.Grade,
                ReferenceKey = raw.TryGetProperty("reference_key", out _) ? attempt.ReferenceKey : previous?.ReferenceKey,
                AiSolution = raw.TryGetProperty("ai_solution", out _) ? attempt.AiSolution : previous?.AiSolution ?? "",
                Conflict = raw.TryGetProperty("conflict", out _) ? attempt.Conflict : previous?.Conflict
            };
        }
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

    private static void RuntimePathsForTask(string directory) => Execution.RuntimePaths.CheckedDirectory(directory, create: true);
    private static JsonElement Parse(JsonNode value) { using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone(); }
}
