using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

public sealed partial class GradingRunner
{
    private async Task<ExamLayout> ClassifyLayoutAsync(JobContext context, GradingInput input, WorkflowModel splitter,
        ExamWorkflowSettings settings, CancellationToken cancellationToken)
    {
        var selected = input.Classifier ?? splitter;
        var credentials = (credentialReader ?? ReadCredentials)(selected.Provider);
        bool direct = new Uri(selected.Provider.BaseUrl).Host.Equals("api.deepseek.com", StringComparison.OrdinalIgnoreCase);
        if (!direct) Options(selected.Provider, selected.Model, null, input.AllowUnknown, WorkflowPrompts.Classify).Validate(selected.Provider.Protocol, true, false);
        await context.ActivityAsync(new JobActivity { Key = "classify", Kind = "classify", Title = "Identify exam format",
            Detail = selected.Provider.Name + " / " + selected.Model.Label }).ConfigureAwait(false);
        var previews = new List<Image>();
        foreach (var page in input.Document.Pages)
        {
            var asset = await library.ReadAssetAsync(page.AssetId, cancellationToken: cancellationToken).ConfigureAwait(false);
            previews.Add((await images.ReadLayoutAsync(asset.Bytes, page.Rotation, cancellationToken).ConfigureAwait(false)).Image);
        }
        var payload = new JsonObject { ["requested_mode"] = JsonNamingPolicy.SnakeCaseLower.ConvertName(input.Document.Mode.ToString()),
            ["page_order"] = DomainJson.ToElement(input.Document.Pages).AsNode() };
        ExamLayout layout;
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                if (direct)
                {
                    using var client = new QuestionSplitter(library);
                    layout = await client.ClassifyAsync(context, selected, credentials.Key, checked((uint)Interlocked.Increment(ref ordinal)),
                        payload, previews, $"exam-layout-{attempt}.json", cancellationToken).ConfigureAwait(false);
                }
                else
                    layout = await CallAsync<ExamLayout>(context, selected.Provider, selected.Model, null, input.AllowUnknown,
                        "classify", WorkflowPrompts.Classify, Parse(payload), previews, settings, new GradingWorkspace(context.JobId), cancellationToken).ConfigureAwait(false);
                _ = layout.Apply(input.Document);
                break;
            }
            catch (Exception error) when (attempt < 3 && error is JsonException or DomainValidationException)
            {
                payload["validation_error"] = error.Message;
                OhMyStudy.Application.Diagnostics.OperationDiagnostics.Record("Workflow.LayoutCorrection", new()
                { ["attempt"] = attempt, ["next_attempt"] = attempt + 1, ["exception"] = error.ToString() }, true);
            }
        }
        await context.CommitAsync(ct => library.SaveGradingLayoutAsync(context.JobId, layout, ct)).ConfigureAwait(false);
        await context.CommitAsync(ct => library.SaveWorkflowArtifactAsync(context.JobId, "exam-layout.json", "application/json",
            Encoding.UTF8.GetBytes(DomainJson.Serialize(layout)), ct)).ConfigureAwait(false);
        await context.ActivityAsync(new JobActivity { Key = "classify", Kind = "classify", Title = "Identify exam format", State = "completed",
            Detail = "Exam format identified.", Text = layout.Reason.Length > 1000 ? layout.Reason[..1000] : layout.Reason, ExamLayout = layout }).ConfigureAwait(false);
        await context.ProgressAsync(5, "Exam format identified.").ConfigureAwait(false);
        return layout;
    }
}
