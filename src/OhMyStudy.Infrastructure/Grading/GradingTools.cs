using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

/// <summary>Closed JSON schemas; recursive questions use local references, never model-provided executable templates.</summary>
public static class GradingTools
{
    public static IReadOnlyList<Tool> Create(bool referenceOnly)
    {
        var result = new List<Tool>
        {
            Define("read_page", "读取当前资料的原页或裁片；坐标均为未旋转原页归一化坐标。", Object(new() { ["page_id"] = String(), ["crop"] = Nullable(Region()), ["rotation"] = Nullable(new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(0, 90, 180, 270) }) }, ["page_id"])),
            Define("save_references", "保存参考答案及匹配；不自动重新批改。", Object(new() { ["references"] = Array(Reference()) }, ["references"])),
            Define("finish", "读取全部输入页后提交逐页覆盖；必须是本轮最后一个调用。", Object(new() { ["coverage"] = Array(Coverage()) }, ["coverage"]))
        };
        if (!referenceOnly)
        {
            result.Add(Define("save_question", "保存完整题目与小问。稳定draft_key重批时复用，教师和手动评分不得覆盖。", QuestionSchema()));
            foreach (var name in new[] { "python", "shell" })
                result.Add(Define(name, name == "python" ? "使用Python进行必要计算；执行前可能等待用户批准。" : "使用Windows PowerShell进行必要计算；执行前可能等待用户批准。", Object(new() { ["code"] = String() }, ["code"])));
        }
        return result;
    }

    private static Tool Define(string name, string description, JsonObject schema) => new() { Name = name, Description = description, Schema = JsonSerializer.SerializeToElement(schema, ProviderSchemaJson.Default.JsonObject) };
    private static JsonObject String() => new() { ["type"] = "string" };
    private static JsonObject Number() => new() { ["type"] = "number", ["minimum"] = 0 };
    private static JsonObject Bool() => new() { ["type"] = "boolean" };
    private static JsonObject Array(JsonNode items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject Nullable(JsonNode value) => new() { ["anyOf"] = new JsonArray(value, new JsonObject { ["type"] = "null" }) };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    private static JsonObject Object(JsonObject properties, string[]? required = null) => new()
    { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray((required ?? properties.Select(p => p.Key).ToArray()).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), ["additionalProperties"] = false };
    private static JsonObject Region() => Object(new() { ["page_id"] = String(), ["x"] = Number(), ["y"] = Number(), ["width"] = Number(), ["height"] = Number() });
    private static JsonObject Grade() => Object(new() { ["verdict"] = Enum("correct", "partial", "incorrect", "unanswered", "uncertain"), ["earned"] = Nullable(Number()), ["possible"] = Nullable(Number()), ["source"] = Enum("teacher", "ai_reference", "ai_solution", "manual"), ["reason"] = String() });
    private static JsonObject Reference() => Object(new() { ["key"] = String(), ["number"] = String(), ["answer"] = String(), ["explanation"] = String(), ["sources"] = Array(Region()), ["matched_draft_key"] = Nullable(String()), ["uncertain_reason"] = Nullable(String()) });
    private static JsonObject Coverage() => Object(new() { ["page_id"] = String(), ["status"] = Enum("covered", "unreadable", "no_questions"), ["question_keys"] = Array(String()), ["reason"] = String() });
    public static JsonObject QuestionSchema()
    {
        var text = Object(new() { ["kind"] = Enum("text"), ["text"] = String() });
        var formula = Object(new() { ["kind"] = Enum("formula"), ["latex"] = String() });
        var table = Object(new() { ["kind"] = Enum("table"), ["rows"] = Array(Array(String())) });
        var image = Object(new() { ["kind"] = Enum("image"), ["region"] = Region(), ["contains_handwriting"] = Bool(), ["caption"] = String() });
        var attempt = Object(new()
        {
            ["student_answer"] = String(),
            ["student_regions"] = Array(Region()),
            ["teacher"] = Nullable(Object(new() { ["raw"] = String(), ["regions"] = Array(Region()), ["grade"] = Nullable(Grade()) })),
            ["reference_key"] = Nullable(String()),
            ["ai_solution"] = String(),
            ["conflict"] = Nullable(String()),
            ["grade"] = Grade()
        });
        var question = Object(new()
        {
            ["draft_key"] = String(),
            ["number"] = String(),
            ["subject"] = String(),
            ["question_type"] = String(),
            ["content"] = Array(new JsonObject { ["anyOf"] = new JsonArray(text, formula, table, image) }),
            ["options"] = Array(String()),
            ["points"] = Nullable(Number()),
            ["standard_answer"] = String(),
            ["explanation"] = String(),
            ["knowledge_points"] = Array(String()),
            ["sources"] = Array(Region()),
            ["uncertainties"] = Array(String()),
            ["attempt"] = Nullable(attempt),
            ["parts"] = Array(new JsonObject { ["$ref"] = "#/$defs/question" })
        });
        // Root content is the question shape; only the recursive definition points back into $defs.
        var root = (JsonObject)question.DeepClone();
        root["$defs"] = new JsonObject { ["question"] = question };
        return root;
    }

    internal static void Fields(JsonElement args, params string[] allowed)
    {
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))) throw new JsonException("工具参数不符合定义。");
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class ProviderSchemaJson : System.Text.Json.Serialization.JsonSerializerContext;
