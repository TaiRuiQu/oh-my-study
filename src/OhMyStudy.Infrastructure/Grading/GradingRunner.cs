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
        var input = job.Input ?? throw new ArgumentException("整卷输入缺失。");
        bool referenceOnly = job.Kind == DiagnosticKind.References;
        var credentials = (credentialReader ?? ReadCredentials)(input.Provider);
        var client = (clientFactory ?? (provider => new ProviderClient(provider.Protocol, provider.BaseUrl)))(input.Provider);
        try { await RunPaperAsync(job, context, input, client, credentials, referenceOnly).ConfigureAwait(false); }
        finally { if (clientFactory is null && client is IDisposable disposable) disposable.Dispose(); }
    }

    private static ProviderCredentials ReadCredentials(Provider provider)
    {
        var bytes = WindowsCredentialStore.Read(provider.KeyRef ?? throw new InvalidOperationException("请先设置模型密钥。"))
            ?? throw new InvalidOperationException("请重新设置模型密钥。");
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
            ["existing_questions"] = JsonNode.Parse(DomainJson.Serialize(existing)),
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
            MaxOutputTokens = 16384,
            Stream = true,
            Thinking = input.Thinking,
            AllowUnknownCapabilities = input.AllowUnknown,
            Extra = extra
        };
        options.Validate(input.Provider.Protocol, true, true);
        var definitions = GradingTools.Create(referenceOnly);
        var viewed = new HashSet<string>(StringComparer.Ordinal);
        var saved = new HashSet<string>(StringComparer.Ordinal);
        for (uint ordinal = 1; ordinal <= 60; ordinal++)
        {
            cancellation.ThrowIfCancellationRequested();
            await context.ProgressAsync(Math.Min(90, ordinal), $"已阅读 {viewed.Count} 页，已保存 {saved.Count} 题。").ConfigureAwait(false);
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
            if (turn.Calls.Count == 0) throw new InvalidOperationException("模型没有保存结果，任务未完成。");
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
                        "read_page" => pageIndex >= 0 ? $"阅读第 {pageIndex + 1} 页" : "阅读页面",
                        "save_paper_info" => "整理试卷信息",
                        "save_question" => "保存题目",
                        "save_references" => "整理参考答案",
                        "finish" => "核对整卷结果",
                        "python" or "shell" => "运行辅助计算",
                        _ => "检查操作"
                    },
                    PageId = pageIndex >= 0 ? pageId : null,
                    Detail = "正在处理",
                    PagesRead = viewed.Count,
                    QuestionsSaved = saved.Count
                };
                await context.ActivityAsync(activity).ConfigureAwait(false);
                var attachments = new List<Image>();
                JsonObject result;
                bool error = false;
                try
                {
                    if (finished) throw new InvalidOperationException("finish后不能继续调用工具。");
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
                            if (index + 1 != turn.Calls.Count) throw new InvalidOperationException("finish必须是最后一个工具调用。");
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
                            if (!executionResult.Succeeded) throw new InvalidOperationException("代码执行未完成。");
                            result = new() { ["stdout"] = executionResult.Stdout }; break;
                        default: throw new InvalidOperationException("当前任务不允许此工具。");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception caught) when (caught is DomainValidationException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException)
                {
                    // Tool validation is useful feedback to the model; transport, credentials and IO failures do not loop.
                    result = new() { ["error"] = caught is DomainValidationException ? caught.Message : "工具参数或业务约束不符合要求，请按定义修正。" };
                    error = true;
                }
                string detail = error ? result["error"]!.GetValue<string>() : call.Name switch
                {
                    "read_page" => call.Arguments.TryGetProperty("crop", out var cropValue) && cropValue.ValueKind == JsonValueKind.Object ? "已查看页面局部" : "已读取原件",
                    "save_paper_info" => result["saved"]!.GetValue<bool>() ? "试卷信息已保存" : "已保留手动填写的试卷信息",
                    "save_question" => "题目已加入题库",
                    "save_references" => "参考答案已保存",
                    "finish" => "页面覆盖与保存结果核对完成",
                    "python" or "shell" => "辅助计算已完成",
                    _ => "操作完成"
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
                await context.ProgressAsync(Math.Min(90, ordinal), $"已阅读 {viewed.Count} 页，已保存 {saved.Count} 题。").ConfigureAwait(false);
                feedback.Add(new() { Call = call, Text = result.ToJsonString(), Images = attachments, IsError = error });
            }
            if (finished) return;
            ProviderProtocol.ContinueWith(input.Provider.Protocol, history, turn, feedback);
        }
        throw new InvalidOperationException("任务达到调用次数上限，已保存的结果会保留。");
    }

    private async Task<JsonObject> ReadPageAsync(GradingInput input, JsonElement arguments, HashSet<string> viewed, List<Image> attachments, CancellationToken cancellationToken)
    {
        GradingTools.Fields(arguments, "page_id", "crop", "rotation");
        var id = arguments.GetProperty("page_id").GetString();
        var page = input.Document.Pages.FirstOrDefault(p => p.Id == id) ?? throw new DomainValidationException("来源页不属于本资料。");
        if (page.PreviewStatus != "ready") throw new DomainValidationException("页面尚未读取。");
        Region? crop = arguments.TryGetProperty("crop", out var cropJson) && cropJson.ValueKind != JsonValueKind.Null ? DomainJson.Deserialize<Region>(cropJson.GetRawText()) : null;
        if (crop is not null)
        {
            if (crop.PageId != page.Id) throw new DomainValidationException("裁片与来源页不一致。");
            GradingValidation.ValidateRegion(crop, input.Document, page.Role);
        }
        ushort rotation = page.Rotation;
        if (arguments.TryGetProperty("rotation", out var rotationJson) && rotationJson.ValueKind != JsonValueKind.Null)
        {
            if (rotationJson.ValueKind != JsonValueKind.Number || !rotationJson.TryGetUInt16(out rotation)) throw new DomainValidationException("方向必须为0、90、180或270度。");
        }
        if (rotation is not (0 or 90 or 180 or 270)) throw new DomainValidationException("方向必须为0、90、180或270度。");
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
        Attempt? attempt = q.Attempt;
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
        var parts = Has("parts") ? q.Parts : old?.Parts ?? q.Parts;
        if (Has("parts"))
        {
            var rawParts = args.GetProperty("parts").EnumerateArray().ToList();
            parts = parts.Select((part, index) => FillOmittedFields(part, rawParts[index], old?.Parts.FirstOrDefault(p => p.DraftKey == part.DraftKey), mode)).ToList();
        }
        return q with
        {
            Subject = Has("subject") ? q.Subject : old?.Subject ?? "",
            QuestionType = Has("question_type") ? q.QuestionType : old?.QuestionType ?? "",
            Options = Has("options") ? q.Options : old?.Options ?? [],
            Points = Has("points") ? q.Points : old?.Points,
            StandardAnswer = Has("standard_answer") ? q.StandardAnswer : old?.StandardAnswer ?? "",
            Explanation = Has("explanation") ? q.Explanation : old?.Explanation ?? "",
            KnowledgePoints = Has("knowledge_points") ? q.KnowledgePoints : old?.KnowledgePoints ?? [],
            Uncertainties = Has("uncertainties") ? q.Uncertainties : old?.Uncertainties ?? [],
            Attempt = attempt,
            Parts = parts
        };
    }

    private static void RuntimePathsForTask(string directory) => Execution.RuntimePaths.CheckedDirectory(directory, create: true);
    private static JsonElement Parse(JsonNode value) { using var document = JsonDocument.Parse(value.ToJsonString()); return document.RootElement.Clone(); }
}
