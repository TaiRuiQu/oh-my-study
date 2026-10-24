using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Providers;

internal static partial class Program
{
    private static async Task PageImageFormats()
    {
        foreach (var protocol in Enum.GetValues<Protocol>())
        {
            await using var fixture = await Fixture.Create(ImportMode.Blank, protocol);
            fixture.Reader.Output = new Image { Mime = "image/jpeg", Base64 = Convert.ToBase64String([255, 216, 255, 217]) };
            var question = Question(fixture.Document, reference: false);
            var client = new DelegateClient(protocol, (round, history, _) =>
            {
                if (round == 1)
                    return Task.FromResult(NativeTurn(protocol, fixture.Document.Pages.Select(page => Read(page)).ToList()));
                var pages = FeedbackObjects(history).Where(value => value["local_file"] is not null).ToList();
                Check(pages.Count == fixture.Document.Pages.Count && pages.All(value => value["local_file"]!.GetValue<string>().EndsWith(".jpg", StringComparison.Ordinal)), "compressed page working files retain their JPEG extension");
                Check(pages.All(value => value["source_file"]!.GetValue<string>().EndsWith(".png", StringComparison.Ordinal)), "compressed page reads retain the original source format");
                Check(history.ToJsonString().Contains("image/jpeg", StringComparison.Ordinal), "compressed page attachments retain their JPEG MIME type");
                return Task.FromResult(NativeTurn(protocol, [Save(question), Finish(Coverage(fixture.Document, question.DraftKey))]));
            });
            Check((await fixture.Run(client)).State == JobState.Completed, "JPEG page feedback completes grading across provider protocols");
        }
    }
}
