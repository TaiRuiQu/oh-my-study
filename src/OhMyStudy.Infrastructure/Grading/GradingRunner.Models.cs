using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Providers;

namespace OhMyStudy.Infrastructure.Grading;

public sealed partial class GradingRunner
{
    private async Task<T> CallAsync<T>(JobContext context, Provider provider, ModelConfig model, Thinking? thinking, bool allowUnknown,
        string stage, string instructions, JsonElement payload, List<Image> attachments, ExamWorkflowSettings settings, GradingWorkspace workspace,
        CancellationToken cancellationToken, Action<T>? validateResult = null)
    {
        var credentials = (credentialReader ?? ReadCredentials)(provider);
        var client = (clientFactory ?? (p => new ProviderClient(p.Protocol, p.BaseUrl, supportsFilesApi: p.SupportsFilesApi)))(provider);
        var history = ProviderProtocol.Initial(provider.Protocol, payload.GetRawText(), attachments);
        Tool[] tools = stage == "solve" ? [PythonTool()] : [];
        var options = Options(provider, model, thinking, allowUnknown, instructions);
        if (provider.Protocol == Protocol.Responses)
        {
            var format = WorkflowOutputSchema.Definition<T>(payload.AsNode());
            format["type"] = "json_schema";
            options = options with { Extra = new Dictionary<string, JsonElement>(options.Extra)
            { ["text"] = Parse(new JsonObject { ["format"] = format }) } };
        }
        else if (provider.Protocol == Protocol.Messages)
            options = options with { Extra = new Dictionary<string, JsonElement>(options.Extra)
            { ["output_config"] = Parse(new JsonObject { ["format"] = new JsonObject { ["type"] = "json_schema", ["schema"] = WorkflowOutputSchema.Schema<T>(payload.AsNode()) } }) } };
        if (stage == "split") options = options with { MaxOutputTokens = Math.Min((ulong)settings.SplitterMaxTokens,
            model.MaxTokens ?? model.MaxOutputTokens ?? (ulong)settings.SplitterMaxTokens) };
        if (stage == "classify") options = options with { MaxOutputTokens = Math.Min(options.MaxOutputTokens, 4096) };
        int maximumRounds = stage == "solve" ? settings.MaxQuestionRounds : stage == "assess" ? 3 : 1;
        try
        {
            options.Validate(provider.Protocol, attachments.Count > 0, tools.Length > 0);
            for (int round = 0; round < maximumRounds; round++)
            {
                uint callOrdinal = checked((uint)Interlocked.Increment(ref ordinal));
                var response = await ModelActivity.CallAsync(context, callOrdinal,
                    progress => client.CallAsync(credentials, options, history, tools, progress, cancellationToken), cancellationToken, maximumAttempts: 1).ConfigureAwait(false);
                var turn = response.Turn;
                var details = new JsonObject { ["stage"] = stage, ["model"] = model.Id, ["provider_id"] = provider.Id,
                    ["usage"] = DomainJson.ToElement(turn.Usage).AsNode(), ["estimated_cost"] = ProviderProtocol.EstimateCost(turn.Usage, model), ["currency"] = model.Currency,
                    ["price_input"] = model.PriceInput, ["price_output"] = model.PriceOutput, ["price_cache_read"] = model.PriceCacheRead, ["price_cache_write"] = model.PriceCacheWrite };
                await context.CommitAsync(ct => library.RecordModelCallAsync(context.JobId, callOrdinal, Parse(details), ct)).ConfigureAwait(false);
                if (turn.Calls.Count == 0)
                {
                    await context.CommitAsync(ct => library.SaveWorkflowArtifactAsync(context.JobId, $"{stage}-{callOrdinal}.json", "application/json", Encoding.UTF8.GetBytes(turn.Text), ct)).ConfigureAwait(false);
                    string? normalized = null;
                    try
                    {
                        var parsed = WorkflowOutputParser.Parse<T>(turn.Text, payload.AsNode());
                        normalized = parsed.Json;
                        if (parsed.Changes.Count > 0)
                        {
                            string artifact = $"{stage}-{callOrdinal}-normalized.json";
                            await context.CommitAsync(ct => library.SaveWorkflowArtifactAsync(context.JobId, artifact, "application/json", Encoding.UTF8.GetBytes(parsed.Json), ct)).ConfigureAwait(false);
                            OperationDiagnostics.Record("Workflow.Normalized", new() { ["stage"] = stage,
                                ["changes"] = DomainJson.ToElement(parsed.Changes).AsNode(), ["artifact"] = artifact });
                        }
                        validateResult?.Invoke(parsed.Value);
                        return parsed.Value;
                    }
                    catch (Exception error) when (error is JsonException or DomainValidationException)
                    {
                        OperationDiagnostics.Record("Workflow.InvalidResult", new() { ["stage"] = stage, ["response"] = turn.Text,
                            ["normalized_response"] = normalized, ["exception"] = error.ToString() }, true);
                        if (round + 1 == maximumRounds) throw;
                        ProviderProtocol.ContinueWith(provider.Protocol, history, turn, []);
                        var correction = ProviderProtocol.Initial(provider.Protocol,
                            "Correct the previous JSON result. Validation error: " + error.Message + " Preserve supplied item keys.", []);
                        foreach (var message in correction) history.Add(message?.DeepClone());
                        continue;
                    }
                }
                var feedback = new List<ToolResult>();
                foreach (var call in turn.Calls)
                {
                    if (stage != "solve" || call.Name != "python") throw new InvalidOperationException("Only the controlled Python calculation tool is allowed while solving a question.");
                    var result = await ExecutePythonAsync(context, call, callOrdinal, feedback.Count, workspace, cancellationToken).ConfigureAwait(false);
                    feedback.Add(new ToolResult { Call = call, Text = result, Images = [], IsError = result.Contains("\"error\":", StringComparison.Ordinal) });
                }
                ProviderProtocol.ContinueWith(provider.Protocol, history, turn, feedback);
            }
            throw new InvalidOperationException("The question reached its configured calculation round limit without a final answer.");
        }
        finally
        {
            try
            {
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await client.DeleteFilesAsync(credentials, cleanup.Token).ConfigureAwait(false);
            }
            catch (Exception error) { OperationDiagnostics.Record("Files.CleanupFailed", new() { ["provider_id"] = provider.Id, ["exception"] = error.ToString() }, true); }
            if (clientFactory is null && client is IDisposable disposable) disposable.Dispose();
        }
    }

    private static RequestOptions Options(Provider provider, ModelConfig model, Thinking? thinking, bool allowUnknown, string instructions)
    {
        string sessionId = Guid.NewGuid().ToString("N");
        ulong defaultOutputTokens = thinking switch
        {
            BudgetThinking budget => checked(budget.Tokens + 8192),
            ResponsesThinking { Effort: "high" or "max" } => 65536,
            _ => 16384
        };
        return new RequestOptions { Model = new() { Id = model.Id, Capabilities = model.Capabilities, Raw = DomainJson.ToElement((string?)null) },
            Stream = true, Thinking = thinking, AllowUnknownCapabilities = allowUnknown,
            MaxOutputTokens = model.MaxTokens ?? Math.Min(model.MaxOutputTokens ?? defaultOutputTokens, defaultOutputTokens),
            Extra = new Dictionary<string, JsonElement> { [provider.Protocol == Protocol.Responses ? "instructions" : "system"] = DomainJson.ToElement(instructions) },
            SessionId = sessionId, PromptCacheKey = provider.Protocol == Protocol.Responses && provider.CodexMode ? sessionId : null };
    }

    private async Task<string> ExecutePythonAsync(JobContext context, ToolCall call, uint callOrdinal, int index, GradingWorkspace workspace, CancellationToken cancellationToken)
    {
        string key = $"python-{callOrdinal}-{index}";
        try
        {
            if (call.Arguments.ValueKind != JsonValueKind.Object || call.Arguments.EnumerateObject().Any(p => p.Name != "code"))
                throw new JsonException("Python arguments must contain only code.");
            string code = call.Arguments.GetProperty("code").GetString() ?? throw new JsonException("Python code is missing.");
            if (code.Length > 16000) throw new DomainValidationException("Python calculations must be at most 16000 characters.");
            var assembly = typeof(GradingRunner).Assembly;
            using var stream = assembly.GetManifestResourceStream("OhMyStudy.Infrastructure.Grading.ControlledPython.py")!;
            using var reader = new StreamReader(stream);
            string program = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            program = program.Replace("SOURCE_JSON", DomainJson.Serialize(DomainJson.Serialize(code)), StringComparison.Ordinal);
            await context.ActivityAsync(new JobActivity { Key = key, Kind = "python", Title = "Calculate with Python", Code = code, Detail = "Processing" }).ConfigureAwait(false);
            await pythonGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            ToolExecutionResult output;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                output = await context.ExecuteAsync(new ExecutionCall { ProtocolVersion = 1, JobId = context.JobId, CallId = key, Tool = ToolKind.Python,
                    Content = program, Arguments = [], WorkingDirectory = workspace.DirectoryPath, TimeoutMs = 30000 }).ConfigureAwait(false);
            }
            finally { pythonGate.Release(); }
            var result = new JsonObject { ["status"] = output.Status, ["stdout"] = output.Stdout, ["stderr"] = output.Stderr, ["exit_code"] = output.ExitCode };
            if (!output.Succeeded) result["error"] = "The controlled Python calculation failed. Inspect stderr and correct the expression.";
            string display = output.Stdout + "\n" + output.Stderr;
            await context.ActivityAsync(new JobActivity { Key = key, Kind = "python", Title = "Calculate with Python", Code = code,
                State = output.Succeeded ? "completed" : "failed", Detail = output.Status, Text = display.Length > 16000 ? display[..16000] : display }).ConfigureAwait(false);
            OperationDiagnostics.Record("Workflow.Python", new() { ["arguments"] = call.Arguments.AsNode(), ["result"] = result.DeepClone() }, !output.Succeeded);
            return result.ToJsonString();
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            OperationDiagnostics.Record("Workflow.PythonFailed", new() { ["arguments"] = call.Arguments.AsNode(), ["exception"] = error.ToString() }, true);
            await context.ActivityAsync(new JobActivity { Key = key, Kind = "python", Title = "Calculate with Python", State = "failed", Detail = error.Message.Length > 1000 ? error.Message[..1000] : error.Message }).ConfigureAwait(false);
            return new JsonObject { ["error"] = error.Message }.ToJsonString();
        }
    }

    private static Tool PythonTool() => new() { Name = "python", Description = "Controlled mathematical Python. Simple assignments and expressions only. Functions: symbols, Symbol, Rational, Integer, Matrix, Eq, solve, linsolve, diff, integrate, limit, simplify, expand, factor, sin, cos, tan, sqrt, log, exp, Abs, N, det, transpose, factorial, comb, gcd, sum, min, max, print. Constants: pi, E, oo. No imports, attributes, shell, files or network.",
        Schema = Parse(new JsonObject { ["type"] = "object", ["properties"] = new JsonObject { ["code"] = new JsonObject { ["type"] = "string" } },
            ["required"] = new JsonArray("code"), ["additionalProperties"] = false }) };
    private static Task StageAsync(JobContext context, Question question, string stage, string title, string state, string detail = "")
    {
        string heading = title + " " + question.Number;
        return context.ActivityAsync(new JobActivity { Key = stage + "-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(question.DraftKey))),
            Kind = stage, Title = heading.Length > 200 ? heading[..200] : heading, State = state, Detail = detail.Length > 1000 ? detail[..1000] : detail });
    }
    private static JsonElement Parse(JsonObject obj) => JsonSerializer.SerializeToElement(obj, WorkflowJsonContext.Default.JsonObject);
    private static ProviderCredentials ReadCredentials(Provider provider) => new(ReadSecret(provider.KeyRef, "Configure the selected model key in Settings."));
    private static string ReadSecret(string? reference, string message)
    {
        var bytes = reference is null ? null : WindowsCredentialStore.Read(reference);
        if (bytes is null) throw new InvalidOperationException(message);
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
}

internal static class WorkflowJson
{
    public static JsonNode? AsNode(this JsonElement value) => JsonNode.Parse(value.GetRawText());
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class WorkflowJsonContext : System.Text.Json.Serialization.JsonSerializerContext;
