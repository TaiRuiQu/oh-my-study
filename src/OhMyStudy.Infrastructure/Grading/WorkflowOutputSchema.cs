using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Schema;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

internal static class WorkflowOutputSchema
{
    public static JsonObject Definition<T>(JsonNode? input = null)
        => new() { ["name"] = typeof(T).Name, ["strict"] = true, ["schema"] = Schema<T>(input) };

    public static JsonObject Schema<T>(JsonNode? input = null)
    {
        var typeInfo = DomainJson.Context.GetTypeInfo(typeof(T)) ?? throw new NotSupportedException($"JSON contract is not registered: {typeof(T)}");
        var schema = typeInfo.GetJsonSchemaAsNode(new JsonSchemaExporterOptions
        {
            TreatNullObliviousAsNonNullable = true,
            TransformSchemaNode = (context, node) =>
            {
                var type = context.TypeInfo.Type;
                if (type == typeof(ExamFormat)) return EnumSchema<ExamFormat>();
                if (type == typeof(ExamPageKind)) return EnumSchema<ExamPageKind>();
                if (type == typeof(Verdict)) return EnumSchema<Verdict>();
                if (type == typeof(GradeSource))
                    return new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("teacher", "ai_solution", "ai_reference") };
                if (type == typeof(double)) return new JsonObject { ["type"] = "number" };
                if (type == typeof(double?)) return new JsonObject { ["type"] = new JsonArray("number", "null") };
                if (node is JsonObject obj)
                {
                    obj.Remove("default");
                    if (obj["properties"] is JsonObject properties)
                    {
                        obj["additionalProperties"] = false;
                        obj["required"] = new JsonArray(properties.Select(p => (JsonNode?)JsonValue.Create(p.Key)).ToArray());
                    }
                }
                return node;
            }
        });
        return Portable(Expand(schema, schema, new HashSet<string>(StringComparer.Ordinal)), input?["allowed_answer_binding_statuses"] as JsonArray).AsObject();
    }

    private static JsonNode Expand(JsonNode node, JsonNode root, HashSet<string> active)
    {
        if (node is JsonObject obj)
        {
            if (obj["$ref"] is JsonValue reference && reference.TryGetValue<string>(out string? pointer))
            {
                if (!pointer.StartsWith("#/", StringComparison.Ordinal) || !active.Add(pointer))
                    throw new NotSupportedException("Workflow output schemas require nonrecursive local references.");
                JsonNode target = root;
                foreach (string part in pointer[2..].Split('/'))
                {
                    string key = part.Replace("~1", "/", StringComparison.Ordinal).Replace("~0", "~", StringComparison.Ordinal);
                    target = target is JsonArray array ? array[int.Parse(key, System.Globalization.CultureInfo.InvariantCulture)]!
                        : target[key] ?? throw new InvalidOperationException("Workflow schema reference was not found: " + pointer);
                }
                var expanded = Expand(target, root, active).AsObject();
                active.Remove(pointer);
                foreach (var pair in obj.Where(p => p.Key != "$ref")) expanded[pair.Key] = pair.Value?.DeepClone();
                return expanded;
            }
            var result = new JsonObject();
            foreach (var pair in obj) result[pair.Key] = pair.Value is null ? null : Expand(pair.Value, root, active);
            return result;
        }
        if (node is JsonArray values)
            return new JsonArray(values.Select(value => value is null ? null : Expand(value, root, active)).ToArray());
        return node.DeepClone();
    }

    private static JsonNode Portable(JsonNode node, JsonArray? bindingStatuses)
    {
        if (node is JsonArray values)
            return new JsonArray(values.Select(value => value is null ? null : Portable(value, bindingStatuses)).ToArray());
        if (node is not JsonObject obj) return node.DeepClone();
        var result = new JsonObject();
        foreach (var pair in obj) result[pair.Key] = pair.Value is null ? null : Portable(pair.Value, bindingStatuses);
        if (result["properties"] is JsonObject properties)
        {
            if (properties.ContainsKey("student_answer") && properties.ContainsKey("grade"))
            {
                properties.Remove("student_answer_edited");
                if (result["required"] is JsonArray required)
                    result["required"] = new JsonArray(required.Where(value => value?.ToString() != "student_answer_edited").Select(value => value?.DeepClone()).ToArray());
            }
            if (properties.ContainsKey("item_key") && properties.ContainsKey("status"))
                properties["status"] = new JsonObject { ["type"] = "string", ["enum"] = bindingStatuses?.DeepClone() ?? new JsonArray("matched", "blank", "unmatched", "none") };
            if (properties.ContainsKey("question_keys") && properties.ContainsKey("status"))
                properties["status"] = new JsonObject { ["type"] = "string", ["enum"] = new JsonArray("covered", "unreadable", "no_questions") };
        }
        if (result["type"] is JsonArray types && types.Any(type => type?.GetValue<string>() is "object" or "array"))
        {
            var alternatives = new JsonArray();
            foreach (var type in types)
            {
                if (type?.GetValue<string>() == "null") alternatives.Add((JsonNode)new JsonObject { ["type"] = "null" });
                else
                {
                    var alternative = result.DeepClone().AsObject();
                    alternative["type"] = type?.DeepClone();
                    alternatives.Add((JsonNode)alternative);
                }
            }
            return new JsonObject { ["anyOf"] = alternatives };
        }
        return result;
    }

    private static JsonObject EnumSchema<T>() where T : struct, Enum => new()
    {
        ["type"] = "string",
        ["enum"] = new JsonArray(Enum.GetValues<T>().Select(value => (JsonNode?)JsonValue.Create(JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString()))).ToArray())
    };
}
