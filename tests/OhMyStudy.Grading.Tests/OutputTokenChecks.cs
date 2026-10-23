using System.Net.Http;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

internal static partial class Program
{
    private static async Task OutputTokenChecks()
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            foreach (ulong? configured in new ulong?[] { null, 4096, 65536 })
            {
                await using var fixture = await Fixture.Create(ImportMode.Blank, protocol);
                var question = Question(fixture.Document, reference: false);
                using var api = new ScriptApi(protocol, (round, body, _) =>
                {
                    string parameter = protocol == Protocol.Responses ? "max_output_tokens" : "max_tokens";
                    Check(body[parameter]!.GetValue<ulong>() == (configured ?? 8192), "each request uses the configured output limit or the existing automatic limit");
                    return Task.FromResult<List<ToolCall>>(round == 1
                        ? [.. fixture.Document.Pages.Select(page => Read(page)), Save(question)]
                        : [Finish(Coverage(fixture.Document, question.DraftKey))]);
                });
                using var http = new HttpClient(api);
                using var client = new ProviderClient(http, protocol, fixture.Input.Provider.BaseUrl);
                await using var queue = await fixture.Queue(client);
                var input = fixture.Input with { Model = fixture.Input.Model with { MaxTokens = configured, MaxOutputTokens = 8192 } };
                var submitted = await queue.SubmitAsync(DiagnosticKind.Grading, Policy, input);
                var job = await Wait(queue, submitted.Id);
                Check(job.State == JobState.Completed && api.Attempts == 2 && job.Input!.Model.MaxTokens == configured,
                    "custom output limits survive job persistence and apply across recognition rounds without silent clamping");
            }
        }
    }
}
