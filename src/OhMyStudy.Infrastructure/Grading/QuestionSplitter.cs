using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Diagnostics;
using OhMyStudy.Infrastructure.Storage;
using OhMyStudy.Infrastructure.Providers;

namespace OhMyStudy.Infrastructure.Grading;

internal sealed class QuestionSplitter(LibraryRepository library) : IDisposable
{
    private readonly HttpClient http = new(new DiagnosticHttpHandler(new SocketsHttpHandler { AllowAutoRedirect = false })) { Timeout = TimeSpan.FromMinutes(8) };

    public Task<QuestionSplitPlan> SplitAsync(JobContext context, ExamWorkflowSettings settings, WorkflowModel selected, string key, uint ordinal,
        string artifact, JsonObject input, IReadOnlyList<Image> pageImages, CancellationToken cancellationToken)
        => RequestAsync<QuestionSplitPlan>(context, selected, key, ordinal, "split", WorkflowPrompts.Split, settings.SplitterMaxTokens,
            artifact, input, pageImages, cancellationToken);

    public Task<ExamLayout> ClassifyAsync(JobContext context, WorkflowModel selected, string key, uint ordinal, JsonObject input,
        IReadOnlyList<Image> pageImages, string artifact, CancellationToken cancellationToken)
        => RequestAsync<ExamLayout>(context, selected, key, ordinal, "classify", WorkflowPrompts.Classify, 4096,
            artifact, input, pageImages, cancellationToken);

    private async Task<T> RequestAsync<T>(JobContext context, WorkflowModel selected, string key, uint ordinal, string stage,
        string instructions, int maximumTokens, string artifact, JsonObject input, IReadOnlyList<Image> pageImages, CancellationToken cancellationToken)
    {
        var format = WorkflowOutputSchema.Definition<T>(input);
        format["type"] = "json_schema";
        var body = new JsonObject
        {
            ["model"] = selected.Model.Id,
            ["instructions"] = instructions,
            ["input"] = ProviderProtocol.Initial(Protocol.Responses, input.ToJsonString(), pageImages),
            ["text"] = new JsonObject { ["format"] = format },
            ["reasoning"] = new JsonObject { ["effort"] = "none" },
            ["max_output_tokens"] = Math.Min((ulong)maximumTokens, selected.Model.MaxTokens ?? selected.Model.MaxOutputTokens ?? (ulong)maximumTokens),
            ["stream"] = false
        };
        using var request = new HttpRequestMessage(HttpMethod.Post, selected.Provider.BaseUrl.TrimEnd('/') + "/responses")
        { Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json") };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Workflow {stage} failed: HTTP {(int)response.StatusCode}; {DiagnosticRedaction.Text(text, [key])}");
        using var json = JsonDocument.Parse(text);
        var root = json.RootElement;
        var usage = ProviderProtocol.ResponsesUsage(root.TryGetProperty("usage", out var responseUsage) ? responseUsage : default);
        var details = new JsonObject { ["stage"] = stage, ["model"] = selected.Model.Id, ["provider_id"] = selected.Provider.Id,
            ["usage"] = DomainJson.ToElement(usage).AsNode(), ["estimated_cost"] = ProviderProtocol.EstimateCost(usage, selected.Model), ["currency"] = selected.Model.Currency };
        await context.CommitAsync(ct => library.RecordModelCallAsync(context.JobId, ordinal, JsonSerializer.SerializeToElement(details, WorkflowJsonContext.Default.JsonObject), ct)).ConfigureAwait(false);
        if (root.TryGetProperty("status", out var status) && status.GetString() != "completed")
        {
            string responseArtifact = artifact[..^5] + "-incomplete-response.json";
            var stoppedDetails = root.TryGetProperty("incomplete_details", out var incomplete) ? incomplete : default;
            await context.CommitAsync(ct => library.SaveWorkflowArtifactAsync(context.JobId, responseArtifact, "application/json", Encoding.UTF8.GetBytes(text), ct)).ConfigureAwait(false);
            OperationDiagnostics.Record("Workflow.Incomplete", new() { ["stage"] = stage,
                ["details"] = stoppedDetails.ValueKind == JsonValueKind.Object ? stoppedDetails.AsNode() : null,
                ["usage"] = responseUsage.ValueKind == JsonValueKind.Object ? responseUsage.AsNode() : null, ["artifact"] = responseArtifact }, true);
            throw new InvalidDataException($"The {stage} response did not complete: {status}; details: {(stoppedDetails.ValueKind == JsonValueKind.Undefined ? "unknown" : stoppedDetails.GetRawText())}; output limit: {body["max_output_tokens"]}.");
        }
        var turn = ProviderProtocol.Parse(Protocol.Responses, root);
        var output = turn.Text;
        if (string.IsNullOrWhiteSpace(output)) throw new InvalidDataException($"The {stage} model returned empty JSON.");
        await context.CommitAsync(ct => library.SaveWorkflowArtifactAsync(context.JobId, artifact, "application/json", Encoding.UTF8.GetBytes(output), ct)).ConfigureAwait(false);
        try
        {
            var parsed = WorkflowOutputParser.Parse<T>(output, input);
            if (parsed.Changes.Count > 0)
            {
                string normalizedArtifact = artifact[..^5] + "-normalized.json";
                await context.CommitAsync(ct => library.SaveWorkflowArtifactAsync(context.JobId, normalizedArtifact, "application/json", Encoding.UTF8.GetBytes(parsed.Json), ct)).ConfigureAwait(false);
                OperationDiagnostics.Record("Workflow.Normalized", new() { ["stage"] = stage,
                    ["changes"] = DomainJson.ToElement(parsed.Changes).AsNode(), ["artifact"] = normalizedArtifact });
            }
            return parsed.Value;
        }
        catch (Exception error) when (error is JsonException or DomainValidationException)
        {
            OperationDiagnostics.Record("Workflow.InvalidPlan", new() { ["stage"] = stage, ["response"] = output, ["exception"] = error.ToString() }, true);
            throw;
        }
    }

    public void Dispose() => http.Dispose();
}
