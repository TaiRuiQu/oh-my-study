using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

internal static class GradingToolArguments
{
    private static readonly string[] AttemptFields = ["student_answer", "student_page_ids", "student_work", "error_analysis", "teacher", "reference_key", "ai_solution", "conflict", "grade"];
    private static readonly string[] GradeFields = ["verdict", "earned", "possible", "source", "reason"];

    internal static void NormalizeQuestion(JsonObject question)
    {
        if (question["items"] is not JsonArray items) return;
        foreach (var item in items.OfType<JsonObject>())
        {
            GradeAliases(item);
            JsonObject? attempt = item["attempt"] as JsonObject;
            if (attempt is not null) GradeAliases(attempt);
            foreach (string field in AttemptFields)
            {
                if (!item.ContainsKey(field)) continue;
                if (attempt is null)
                {
                    if (item.ContainsKey("attempt")) throw new JsonException("Cannot combine a non-object attempt with item-level answer fields.");
                    item["attempt"] = attempt = new JsonObject();
                }
                Move(item, field, attempt, field);
            }
            if (attempt is not null) NormalizeAttempt(attempt);
            if (item["options"] is not JsonArray options) continue;
            for (int index = 0; index < options.Count; index++)
            {
                if (options[index] is not JsonObject option) continue;
                Fields(option, "label", "content");
                string label = Text(option["label"], "option label");
                string content = ContentText(option["content"]);
                options[index] = string.IsNullOrWhiteSpace(label) ? content : $"{label.TrimEnd('.', '、', ' ')}. {content}";
            }
        }
    }

    private static void GradeAliases(JsonObject value)
    {
        foreach (string alias in new[] { "grading", "ai_grade", "ai_grading" })
            Move(value, alias, value, "grade");
    }

    private static void NormalizeAttempt(JsonObject attempt)
    {
        foreach (string field in GradeFields)
        {
            if (!attempt.ContainsKey(field)) continue;
            if (attempt["grade"] is not JsonObject)
            {
                if (attempt.ContainsKey("grade")) throw new JsonException("Cannot combine a non-object grade with flattened grading fields.");
                attempt["grade"] = new JsonObject();
            }
            Move(attempt, field, attempt["grade"]!.AsObject(), field);
        }
        if (attempt.Remove("student_work", out var work))
        {
            string text = ContentText(work);
            string answer = attempt.ContainsKey("student_answer") ? Text(attempt["student_answer"], "student_answer") : "";
            attempt["student_answer"] = string.IsNullOrWhiteSpace(answer) ? text
                : string.IsNullOrWhiteSpace(text) || answer == text ? answer : answer + "\n" + text;
        }
        if (attempt["grade"] is not JsonObject grade) return;
        JsonNode? source = grade["source"];
        if (source is JsonObject wrapped)
        {
            Fields(wrapped, "kind");
            source = wrapped["kind"];
        }
        string name = source is null ? "ai" : Text(source, "grade.source");
        if (name.Equals("ai", StringComparison.OrdinalIgnoreCase) || name is "model" or "ai_inferred")
            name = attempt["reference_key"] is JsonValue reference && reference.TryGetValue<string>(out var key) && !string.IsNullOrWhiteSpace(key)
                ? "ai_reference" : "ai_solution";
        if (name is not ("teacher" or "ai_reference" or "ai_solution" or "manual"))
            throw new JsonException("grade.source must be teacher, ai_reference, ai_solution, or manual. Use ai_reference for a matched reference_key and ai_solution for an independent solution.");
        grade["source"] = name;
    }

    private static void Move(JsonObject from, string field, JsonObject to, string target)
    {
        if (!from.TryGetPropertyValue(field, out var value)) return;
        if (to.TryGetPropertyValue(target, out var existing) && !JsonNode.DeepEquals(existing, value))
            throw new JsonException($"Conflicting values for {field} and {target}. Provide one consistent value using the tool definition.");
        from.Remove(field);
        to[target] = value;
    }

    private static string ContentText(JsonNode? value)
    {
        if (value is JsonValue) return Text(value, "text content");
        if (value is not JsonArray blocks) throw new JsonException("Expected a string or an array of text/formula blocks.");
        var texts = new List<string>();
        foreach (var block in blocks)
        {
            if (block is not JsonObject content) throw new JsonException("Expected a text or formula block.");
            string kind = Text(content["kind"], "content.kind");
            switch (kind)
            {
                case "text":
                    Fields(content, "kind", "text");
                    texts.Add(Text(content["text"], "content.text"));
                    break;
                case "formula":
                    Fields(content, "kind", "latex");
                    texts.Add(QuestionText.Delimit(Text(content["latex"], "content.latex")));
                    break;
                default: throw new JsonException("Only text and formula blocks can be converted to a string. Keep other content in stem or prompt.");
            }
        }
        return string.Join('\n', texts);
    }

    private static string Text(JsonNode? value, string field) => value is JsonValue scalar && scalar.TryGetValue<string>(out var text)
        ? text : throw new JsonException($"{field} must be a string.");

    private static void Fields(JsonObject value, params string[] allowed)
    {
        if (value.Any(property => !allowed.Contains(property.Key, StringComparer.Ordinal)))
            throw new JsonException("Unsupported fields in structured text. Match the tool definition; content cannot be discarded.");
    }
}
