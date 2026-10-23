using System.Text.Json.Nodes;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

internal static class LegacyQuestionFormat
{
    public static string UpgradeQuestion(string json)
    {
        var node = JsonNode.Parse(json)?.AsObject() ?? throw new InvalidDataException("A saved question is empty.");
        if (node.ContainsKey("items")) return DomainJson.Serialize(DomainJson.Deserialize<Question>(json));
        var parts = Array(node, "parts");
        var stem = (JsonArray)Array(node, "content").DeepClone();
        var items = new JsonArray();
        var knowledge = new List<string>(); var uncertainties = new List<string>(); var sources = new List<string>();
        Collect(node, knowledge, uncertainties, sources);
        if (parts.Count == 0) items.Add((JsonNode)Item(node, "", new JsonArray()));
        else
        {
            AppendOptions(stem, node);
            Flatten(parts, "", new JsonArray(), items, knowledge, uncertainties, sources);
        }
        var question = new JsonObject
        {
            ["draft_key"] = node["draft_key"]?.DeepClone(),
            ["number"] = node["number"]?.DeepClone(),
            ["subject"] = node["subject"]?.DeepClone() ?? "",
            ["question_type"] = node["question_type"]?.DeepClone() ?? "",
            ["stem"] = stem,
            ["sources"] = new JsonArray(sources.Select(source => JsonNode.Parse(source)).ToArray()),
            ["knowledge_points"] = Strings(knowledge),
            ["uncertainties"] = Strings(uncertainties),
            ["items"] = items,
            ["notes"] = ""
        };
        return DomainJson.Serialize(DomainJson.Deserialize<Question>(question.ToJsonString()));
    }

    public static string UpgradeReferences(string json)
    {
        var references = JsonNode.Parse(json)?.AsArray() ?? throw new InvalidDataException("Saved reference answers are empty.");
        foreach (var reference in references.OfType<JsonObject>())
        {
            if (!reference.ContainsKey("matched_draft_key")) continue;
            var key = reference["matched_draft_key"]?.DeepClone();
            reference.Remove("matched_draft_key");
            reference["matched_item_key"] = key;
        }
        return DomainJson.Serialize(DomainJson.Deserialize<List<ReferenceAnswer>>(references.ToJsonString()));
    }

    private static void Flatten(JsonArray parts, string prefix, JsonArray carried, JsonArray items, List<string> knowledge, List<string> uncertainties, List<string> sources)
    {
        foreach (var part in parts.OfType<JsonObject>())
        {
            Collect(part, knowledge, uncertainties, sources);
            string number = part["number"]?.GetValue<string>() ?? "";
            string label = number.StartsWith(prefix, StringComparison.Ordinal) ? number : prefix + number;
            var prompt = new JsonArray(carried.Select(content => content?.DeepClone()).ToArray());
            carried.Clear();
            foreach (var content in Array(part, "content")) prompt.Add(content?.DeepClone());
            var nested = Array(part, "parts");
            if (nested.Count == 0) { items.Add((JsonNode)Item(part, label, prompt)); continue; }
            AppendOptions(prompt, part);
            Flatten(nested, label, prompt, items, knowledge, uncertainties, sources);
        }
    }

    private static JsonObject Item(JsonObject node, string label, JsonArray prompt) => new()
    {
        ["key"] = node["draft_key"]?.DeepClone(),
        ["label"] = label,
        ["prompt"] = prompt,
        ["options"] = node["options"]?.DeepClone() ?? new JsonArray(),
        ["points"] = node["points"]?.DeepClone(),
        ["answer"] = node["standard_answer"]?.DeepClone() ?? "",
        ["explanation"] = node["explanation"]?.DeepClone() ?? "",
        ["attempt"] = node["attempt"]?.DeepClone()
    };

    private static void AppendOptions(JsonArray content, JsonObject node)
    {
        var options = Array(node, "options").Select(option => option?.GetValue<string>()).Where(option => !string.IsNullOrWhiteSpace(option)).ToList();
        if (options.Count != 0) content.Add((JsonNode)new JsonObject { ["kind"] = "text", ["text"] = "\n" + string.Join("\n", options) });
    }

    private static void Collect(JsonObject node, List<string> knowledge, List<string> uncertainties, List<string> sources)
    {
        foreach (var point in Array(node, "knowledge_points")) if (point?.GetValue<string>() is { } text && !knowledge.Contains(text)) knowledge.Add(text);
        foreach (var issue in Array(node, "uncertainties")) if (issue?.GetValue<string>() is { } text && !uncertainties.Contains(text)) uncertainties.Add(text);
        foreach (var source in Array(node, "sources")) if (source?.ToJsonString() is { } text && !sources.Contains(text)) sources.Add(text);
    }

    private static JsonArray Array(JsonObject node, string name) => node[name] as JsonArray ?? [];
    private static JsonArray Strings(IEnumerable<string> values) => new(values.Select(value => (JsonNode?)JsonValue.Create(value)).ToArray());
}
