using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Grading;

internal static partial class Program
{
    private static async Task ModelContentChecks()
    {
        string tablePreview = QuestionText.Reading([new TableContent { Rows = [["合并", "<"], ["^", "值"]] }]);
        Check(!tablePreview.Contains('^') && !tablePreview.Contains('<') && tablePreview.Contains("合并", StringComparison.Ordinal)
            && tablePreview.Contains("值", StringComparison.Ordinal), "text previews omit table merge markers while retaining cell content");
        var schema = GradingTools.QuestionSchema();
        var variants = schema["properties"]!["stem"]!["items"]!["anyOf"]!.AsArray();
        JsonObject Variant(string kind) => variants.Single(node => node!["properties"]!["kind"]!["enum"]![0]!.GetValue<string>() == kind)!.AsObject();
        var imageSchema = Variant("image");
        var imageWidth = imageSchema["properties"]!["width_percent"]!;
        Check(imageWidth["minimum"]!.GetValue<int>() == 1 && imageWidth["maximum"]!.GetValue<int>() == 100
            && imageWidth["default"]!.GetValue<int>() == 100 && !imageSchema["required"]!.AsArray().Any(value => value!.GetValue<string>() == "width_percent"),
            "image schema exposes optional width_percent with inclusive bounds and a 100 default");
        var mermaidSchema = Variant("mermaid");
        Check(mermaidSchema["properties"]!["code"]!["maxLength"]!.GetValue<int>() == 32768
            && mermaidSchema["properties"]!["width_percent"]!["default"]!.GetValue<int>() == 100
            && mermaidSchema["required"]!.AsArray().Select(value => value!.GetValue<string>()).ToHashSet().SetEquals(["kind", "region", "code"]),
            "Mermaid schema exposes required evidence and code with optional caption and width");

        string instructions = GradingPrompts.Instructions(ImportMode.AiGrading, false, GradingModelProfile.Select("synthetic-model"));
        string[] guidance = ["Markdown tables", "TableContent", "MermaidContent", "Mermaid flowchart code", "flowchart TD|LR|BT|RL", "simple node shapes", "labeled arrows", "raw Mermaid without fenced code blocks", "subgraphs", "classDef/style/linkStyle", "click callbacks", "Markdown-in-node", "other diagram types", "Matplotlib", "asset_id",
            "{font:sans}...{/font}", "{font:serif}...{/font}", "{font:kai}...{/font}", "Microsoft YaHei", "NSimSun", "KaiTi",
            "^=merge up", "<=merge left", ">=merge right", "entire cell content", "width_percent", "default 100", "use 50"];
        Check(guidance.All(instructions.Contains), "model instructions teach structured tables, Mermaid, Matplotlib, fonts, merge markers, and image widths");

        await using var fixture = await Fixture.Create(ImportMode.Blank, Protocol.Responses);
        var material = fixture.Document.Pages.Single(page => page.Role == "material");
        var reference = fixture.Document.Pages.Single(page => page.Role == "reference");
        var region = Region(material);
        var image = new OhMyStudy.Domain.ImageContent { Region = region, Caption = "Geometry evidence", WidthPercent = 50 };
        var mermaid = new MermaidContent { Region = region, Code = "flowchart TD\n    A[Start] --> B[End]", Caption = "Flow of the proof", WidthPercent = 75 };
        var valid = Question(fixture.Document, "model-content", reference: false) with
        {
            Stem = [new TextContent { Text = "Structured content" }, image, mermaid],
            Items = [Question(fixture.Document, "model-content", reference: false).Items[0] with
            {
                Prompt = [image with { WidthPercent = 25 }, mermaid with { WidthPercent = 100 }]
            }]
        };
        var legacy = valid with { DraftKey = "legacy-content", Number = "legacy", Items = [valid.Items[0] with { Key = "legacy-content" }] };
        Question VariantQuestion(Question source, string key) => source with { DraftKey = key, Number = key, Items = [source.Items[0] with { Key = key }] };
        var invalidImageWidth = VariantQuestion(valid, "invalid-image-width") with { Stem = [image with { WidthPercent = 0 }] };
        var invalidMermaidWidth = VariantQuestion(valid, "invalid-mermaid-width") with
        {
            Items = [valid.Items[0] with { Key = "invalid-mermaid-width", Prompt = [mermaid with { WidthPercent = 101 }] }]
        };
        var invalidImageRegion = VariantQuestion(valid, "invalid-image-region") with
        {
            Items = [valid.Items[0] with { Key = "invalid-image-region", Prompt = [image with { Region = Region(reference) }] }]
        };
        var invalidMermaidRegion = VariantQuestion(valid, "invalid-mermaid-region") with
        {
            Stem = [mermaid with { Region = region with { PageId = Guid.NewGuid().ToString() } }]
        };
        var emptyMermaid = VariantQuestion(valid, "empty-mermaid") with { Stem = [mermaid with { Code = " " }] };
        var longMermaid = VariantQuestion(valid, "long-mermaid") with { Stem = [mermaid with { Code = new string('x', 32769) }] };
        JsonNode Payload(Question question) => JsonNode.Parse(DomainJson.Serialize(question))!;
        var legacyPayload = Payload(legacy);
        RemoveWidths(legacyPayload);
        var client = new DelegateClient(Protocol.Responses, (round, history, _) =>
        {
            List<ToolCall> calls = round switch
            {
                1 => fixture.Document.Pages.Select(page => Read(page)).ToList(),
                2 => [Call("save_questions", Args("questions", new JsonArray(Payload(valid), legacyPayload,
                    Payload(invalidImageWidth), Payload(invalidMermaidWidth), Payload(invalidImageRegion), Payload(invalidMermaidRegion),
                    Payload(emptyMermaid), Payload(longMermaid))))],
                3 => [Finish(Coverage(fixture.Document, valid.DraftKey, legacy.DraftKey))],
                _ => throw new InvalidOperationException("Unexpected model content round")
            };
            return Task.FromResult(NativeTurn(Protocol.Responses, calls));
        });
        var job = await fixture.Run(client);
        var saved = await fixture.Library.GradingQuestionsAsync(fixture.Document.Id);
        if (job.State != JobState.Completed)
        {
            var events = await fixture.Library.ListJobEventsAsync(job.Id);
            var failures = events.Where(entry => entry.Kind == "activity").Select(entry => DomainJson.Deserialize<JobActivity>(entry.Payload.GetRawText()))
                .Where(activity => activity.State == "failed").Select(activity => activity.Kind + ": " + activity.Detail);
            throw new InvalidOperationException("Model content task failed with " + saved.Count + " saves: " + string.Join(" | ", failures));
        }
        Check(job.State == JobState.Completed, "valid image widths and Mermaid content complete the synthetic save loop");
        Check(saved.Count == 2 && saved.All(entry => entry.Question.DraftKey is "model-content" or "legacy-content"),
            "invalid widths, material regions, and Mermaid code are rejected without rolling back valid saves");
        var stored = saved.Single(entry => entry.Question.DraftKey == valid.DraftKey).Question;
        var storedImage = stored.Stem.OfType<OhMyStudy.Domain.ImageContent>().Single();
        var storedMermaid = stored.Stem.OfType<MermaidContent>().Single();
        Check(storedImage.WidthPercent == 50 && storedImage.Region == region && storedMermaid.WidthPercent == 75
            && storedMermaid.Region == region && storedMermaid.Code == mermaid.Code && storedMermaid.Caption == mermaid.Caption
            && stored.Items[0].Prompt.OfType<OhMyStudy.Domain.ImageContent>().Single().WidthPercent == 25
            && stored.Items[0].Prompt.OfType<MermaidContent>().Single().Region == region,
            "save roundtrip preserves image and Mermaid widths, code, captions, and stem/item evidence regions");
        var legacyStored = saved.Single(entry => entry.Question.DraftKey == legacy.DraftKey).Question;
        Check(legacyStored.Stem.Concat(legacyStored.Items.SelectMany(item => item.Prompt)).All(block => block switch
        {
            OhMyStudy.Domain.ImageContent content => content.WidthPercent == 100,
            MermaidContent content => content.WidthPercent == 100,
            _ => true
        }), "legacy omitted image and Mermaid widths deserialize as 100");
        var encoded = DomainJson.Serialize(stored);
        Check(DomainJson.Serialize(DomainJson.Deserialize<Question>(encoded)) == encoded, "source-generated JSON roundtrips polymorphic Mermaid content");
        Check(QuestionText.Reading(stored.Stem).Contains(mermaid.Caption, StringComparison.Ordinal)
            && QuestionText.Plain(stored.Stem).Contains(mermaid.Code, StringComparison.Ordinal)
            && QuestionText.Search(stored).Contains(mermaid.Code, StringComparison.Ordinal)
            && QuestionText.Preview(stored).Contains(mermaid.Caption, StringComparison.Ordinal),
            "reading, plain text, search, and preview expose useful Mermaid text");
    }

    private static void RemoveWidths(JsonNode? node)
    {
        if (node is JsonArray array)
        {
            foreach (var child in array) RemoveWidths(child);
            return;
        }
        if (node is not JsonObject value) return;
        value.Remove("width_percent");
        foreach (var property in value) RemoveWidths(property.Value);
    }
}
