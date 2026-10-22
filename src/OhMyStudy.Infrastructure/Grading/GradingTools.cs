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
            Define("read_page", "Read a full page or crop from the current document. Read full pages before saving; use crops to inspect details. Returns an image plus page_id, rotation, crop, displayed width/height, and coordinate_system. Only a full-page read counts toward finish coverage. All regions remain relative to the unrotated full source page.", Object(new()
            {
                ["page_id"] = String("Exact ID from document.pages; never a page number or file path."),
                ["crop"] = Describe(Nullable(Region()), "Omit or use null for the full page. For a crop, use the same page_id and normalized coordinates on the unrotated full page."),
                ["rotation"] = Describe(Nullable(new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(0, 90, 180, 270) }), "Clockwise display rotation in degrees. Omit or use null to keep the page's configured rotation. Coordinates do not rotate.")
            }, ["page_id"])),
            Define("save_references", "Replace the current reference-answer list after reading its source pages. Verify matches by number and content; give a reason for unmatched answers. Send the complete list on every call, preserving keys already used by saved questions. Does not regrade questions. Returns saved=true on success.", Object(new()
            {
                ["references"] = Describe(Array(Reference()), "Complete reference-answer list, at most 1000 entries. Each key must be unique, and each question or part can match at most one entry.")
            }, ["references"])),
            Define("finish", "Validate page coverage and saved results, then complete the task. Call only after reading every required page in full and resolving save errors; this must be the final tool call in the response. Whole-paper tasks must save every top-level question in this run; all reference matches must name saved questions or parts. Reference-only tasks cover reference pages only. Returns finished=true on success; an error means the task is still incomplete.", Object(new()
            {
                ["coverage"] = Describe(Array(Coverage()), "Exactly one entry per document page for whole-paper tasks, or per reference page for reference-only tasks. No duplicate or missing page IDs.")
            }, ["coverage"]))
        };
        if (!referenceOnly)
        {
            result.Add(Define("save_paper_info", "Save the paper's subject and total scores before finish. Use explicit totals or complete grading without double-counting parents and parts. Never guess unknown scores. Returns saved=true, or saved=false with manual_values_kept=true when user-edited paper information is preserved.", Object(new()
            {
                ["subject"] = Describe(Nullable(String()), "Subject inferred from this paper, in Simplified Chinese; use a general-studies label for mixed subjects or null if unknown."),
                ["earned"] = Describe(Nullable(Number()), "Total earned points. Use null when unknown, when possible is unknown, or for blank papers. Cannot exceed possible."),
                ["possible"] = Describe(Nullable(Number()), "Total possible points, or null when unknown. Omitted score fields are treated as null.")
            }, ["subject"])));
            result.Add(Define("save_question", "Save one top-level question with its complete part tree after reading its sources and saving applicable references. Reuse stable draft_key values on regrading. Omitted auxiliary question/attempt fields preserve saved values; explicit empty values replace them. Existing manual grades and explicit teacher results in teacher mode are protected. Returns saved_id and draft_key on success.", QuestionSchema()));
            foreach (var name in new[] { "python", "shell" })
                result.Add(Define(name, name == "python"
                    ? "Run Python code for calculations needed to solve or check this paper; prefer this tool for numerical work. Returns stdout on success. Execution may wait for user approval and has a 60-second timeout. Do not access unrelated files, credentials, services, or system settings."
                    : "Run Windows PowerShell code only when a calculation needed for this paper requires it. Prefer python for numerical work. Returns stdout on success. Execution may wait for user approval and has a 60-second timeout. Do not access unrelated files, credentials, services, or system settings.",
                    Object(new() { ["code"] = String("Complete code in this tool's language. Print the calculation result to stdout. The application supplies the working directory; no path or command-line arguments are accepted separately.") }, ["code"])));
        }
        return result;
    }

    private static Tool Define(string name, string description, JsonObject schema) => new() { Name = name, Description = description, Schema = JsonSerializer.SerializeToElement(schema, ProviderSchemaJson.Default.JsonObject) };
    private static JsonObject Describe(JsonObject schema, string description) { schema["description"] = description; return schema; }
    private static JsonObject String(string? description = null) => description is null ? new() { ["type"] = "string" } : new() { ["type"] = "string", ["description"] = description };
    private static JsonObject Number() => new() { ["type"] = "number", ["minimum"] = 0 };
    private static JsonObject Bool() => new() { ["type"] = "boolean" };
    private static JsonObject Array(JsonNode items) => new() { ["type"] = "array", ["items"] = items };
    private static JsonObject Nullable(JsonNode value) => new() { ["anyOf"] = new JsonArray(value, new JsonObject { ["type"] = "null" }) };
    private static JsonObject Enum(params string[] values) => new() { ["type"] = "string", ["enum"] = new JsonArray(values.Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()) };
    private static JsonObject Object(JsonObject properties, string[]? required = null) => new()
    { ["type"] = "object", ["properties"] = properties, ["required"] = new JsonArray((required ?? properties.Select(p => p.Key).ToArray()).Select(v => (JsonNode?)JsonValue.Create(v)).ToArray()), ["additionalProperties"] = false };
    private static JsonObject Region() => Describe(Object(new()
    {
        ["page_id"] = String("Exact source page ID. Question/attempt regions require role=material; reference-answer regions require role=reference."),
        ["x"] = Describe(Number(), "Left edge divided by unrotated source width: 0 <= x < 1."),
        ["y"] = Describe(Number(), "Top edge divided by unrotated source height: 0 <= y < 1."),
        ["width"] = Describe(Number(), "Positive fraction of unrotated source width; x + width <= 1."),
        ["height"] = Describe(Number(), "Positive fraction of unrotated source height; y + height <= 1.")
    }), "Rectangle on the full unrotated source page, never in pixels or relative to a crop or rotated display.");
    private static JsonObject Grade() => Object(new()
    {
        ["verdict"] = Describe(Enum("correct", "partial", "incorrect", "unanswered", "uncertain"), "correct=fully correct; partial=partly correct; incorrect=wrong; unanswered=visibly blank on an answered paper; uncertain=unreadable or insufficient evidence."),
        ["earned"] = Describe(Nullable(Number()), "Earned points, or null if unknown. Requires known possible. Correct equals possible; partial is strictly between zero and possible; incorrect/unanswered are zero; uncertain must be null."),
        ["possible"] = Describe(Nullable(Number()), "Maximum points, or null if unknown. Must equal the question's points when both are known."),
        ["source"] = Describe(Enum("teacher", "ai_reference", "ai_solution", "manual"), "teacher for explicit teacher results; ai_reference for a verified matched reference; ai_solution otherwise. Never create a manual grade; preserve an existing user-edited one."),
        ["reason"] = String("Brief grading explanation in Simplified Chinese. Explain uncertainty; preserve teacher deduction text when a numerical score cannot be determined.")
    }, ["verdict", "source"]);
    private static JsonObject Reference() => Object(new()
    {
        ["key"] = String("Stable unique reference-answer key; reuse an existing key for the same answer."),
        ["number"] = String("Question or part number as printed in the reference."),
        ["answer"] = String("Nonempty answer transcribed from the reference, preserving its language and LaTeX notation."),
        ["explanation"] = String("Explanation supported by the reference; omit when unavailable."),
        ["sources"] = Describe(Array(Region()), "Nonempty evidence regions on reference pages, including continuations."),
        ["matched_draft_key"] = Describe(Nullable(String()), "Verified question or part draft_key; null or omitted if unmatched. In reference-only tasks, match only existing saved keys."),
        ["uncertain_reason"] = Describe(Nullable(String()), "Required nonempty reason when unmatched; null or omitted for a verified match.")
    }, ["key", "number", "answer", "sources"]);
    private static JsonObject Coverage() => Object(new()
    {
        ["page_id"] = String("Exact ID of a required page already read in full during this run."),
        ["status"] = Describe(Enum("covered", "unreadable", "no_questions"), "covered when all relevant content is accounted for; unreadable when relevant content cannot be read; no_questions only when there are no questions to extract."),
        ["question_keys"] = Describe(Array(String()), "For covered material pages, all saved question/part draft keys whose sources include this page, including continuations. Use an empty list when no question keys apply."),
        ["reason"] = String("Nonempty explanation required for unreadable or no_questions; optional for covered.")
    }, ["page_id", "status"]);
    public static JsonObject QuestionSchema()
    {
        var text = Object(new() { ["kind"] = Describe(Enum("text"), "Text content block."), ["text"] = String("Transcribed question text in the source language; exclude student answers and teacher marks.") });
        var formula = Object(new() { ["kind"] = Describe(Enum("formula"), "Mathematical formula block."), ["latex"] = String("Formula in LaTeX, without surrounding Markdown math delimiters.") });
        var table = Object(new() { ["kind"] = Describe(Enum("table"), "Table content block."), ["rows"] = Describe(Array(Array(String())), "Rows in reading order; each row contains its cell texts in column order.") });
        var image = Object(new()
        {
            ["kind"] = Describe(Enum("image"), "Diagram, figure, or content retained as an image."),
            ["region"] = Describe(Region(), "Source region for the diagram or unreadable content on a material page."),
            ["contains_handwriting"] = Describe(Bool(), "True if this image region contains handwriting."),
            ["caption"] = String("Brief description in Simplified Chinese; omit if unnecessary.")
        }, ["kind", "region"]);
        var attempt = Object(new()
        {
            ["student_answer"] = String("Student response only, preserving its language. Do not include teacher marks, reference answers, or AI solutions."),
            ["student_regions"] = Describe(Array(Region()), "Material-page regions showing the student's response."),
            ["teacher"] = Describe(Nullable(Object(new()
            {
                ["raw"] = String("Verbatim teacher comments or deduction marks; do not invent missing text."),
                ["regions"] = Describe(Array(Region()), "Material-page regions showing teacher marks; omit when unavailable."),
                ["grade"] = Describe(Nullable(Grade()), "Explicit teacher result, with source=teacher. In teacher mode its verdict/scores/source must agree with attempt.grade. Omit or use null if unknown.")
            }, [])), "Teacher annotations, separate from the student response. Omit when unavailable; explicit null clears this field unless protected."),
            ["reference_key"] = Describe(Nullable(String()), "Key of a saved reference matched to this exact draft_key. Required when grading by a matched reference; null when none applies."),
            ["ai_solution"] = String("Your own solution in Simplified Chinese, kept separate from the student and reference answers."),
            ["conflict"] = Describe(Nullable(String()), "Explain disagreement with the matched reference while still grading by that reference; null when there is no conflict."),
            ["grade"] = Describe(Grade(), "Verdict and scores based on teacher priority, a matched reference, or your solution. Omission preserves the saved grade or defaults to uncertain.")
        }, []);
        var question = Object(new()
        {
            ["draft_key"] = String("Nonempty stable key, unique across all questions and parts, at most 128 UTF-8 bytes. Reuse existing keys on regrading."),
            ["number"] = String("Nonempty question or part number, preserving the printed label."),
            ["subject"] = String("Subject label in Simplified Chinese; omit if unknown."),
            ["question_type"] = String("Question type in Simplified Chinese; omit if unknown."),
            ["content"] = Describe(Array(new JsonObject { ["anyOf"] = new JsonArray(text, formula, table, image) }), "Nonempty content blocks in reading order, retaining tables, formulas, and diagrams."),
            ["options"] = Describe(Array(String()), "Answer options in printed order, including their labels; omit if unavailable."),
            ["points"] = Describe(Nullable(Number()), "Maximum points for this question or part; null if unknown. Parent points must equal the sum of known part points when all are known."),
            ["standard_answer"] = String("Verified answer, kept separate from student work. Omit when unknown."),
            ["explanation"] = String("Solution explanation in Simplified Chinese, without student work; omit when unavailable."),
            ["knowledge_points"] = Describe(Array(String()), "Relevant knowledge-point labels in Simplified Chinese; omit if unsupported."),
            ["sources"] = Describe(Array(Region()), "Nonempty material-page regions covering the question, including every continuation page."),
            ["uncertainties"] = Describe(Array(String()), "Specific unresolved transcription or interpretation issues in Simplified Chinese; retain image evidence instead of guessing."),
            ["attempt"] = Describe(Nullable(attempt), "Must be null for every question/part on a blank paper. For answered papers, keep a nonnull attempt; distinguish visibly unanswered from unreadable/uncertain. Omitted fields preserve existing information."),
            ["parts"] = Describe(Array(new JsonObject { ["$ref"] = "#/$defs/question" }), "Complete ordered part tree; use unique stable keys at every level. Preserve protected parts on regrading. Limit: 8 nested levels and 1000 nodes per top-level tree; serialized question at most 1 MiB.")
        }, ["draft_key", "number", "content", "sources"]);
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
