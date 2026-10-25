using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

/// <summary>Closed JSON schemas without references or model-provided executable templates.</summary>
public static class GradingTools
{
    public static IReadOnlyList<Tool> Create(bool referenceOnly) => Create(referenceOnly, GradingModelProfile.Select(""));

    internal static IReadOnlyList<Tool> Create(bool referenceOnly, GradingModelProfile profile)
    {
        var result = new List<Tool>
        {
            Define("read_pages", "Read full pages or crops. Returns ordered page metadata and images. Only full-page reads count toward completion.", Object(new()
            {
                ["pages"] = Describe(Array(Object(new()
                {
                    ["page_id"] = String("Exact ID from document.pages."),
                    ["crop"] = Describe(Nullable(Region()), "Omit for a full page; crop coordinates use the unrotated source page."),
                    ["rotation"] = Describe(Nullable(new JsonObject { ["type"] = "integer", ["enum"] = new JsonArray(0, 90, 180, 270) }), "Clockwise display rotation; omit to use the page's configured rotation.")
                }, ["page_id"])), "Pages or crops to read in order.")
            })),
            Define("save_references", "Replace the complete reference-answer list, preserving existing keys. Verify item matches; explain unmatched answers. Does not regrade questions.", Object(new()
            {
                ["references"] = Describe(Array(Reference()), "Complete reference-answer list, at most 1000 entries. Each key must be unique, and each item can match at most one entry.")
            }, ["references"])),
            Define("finish", "Validate coverage and complete the task. Call last, after full-page reads and successful saves. Whole-paper tasks must save every question in this run. Reference matches must name saved item keys.", Object(new()
            {
                ["coverage"] = Describe(Array(Coverage()), "Exactly one entry per document page for whole-paper tasks, or per reference page for reference-only tasks. No duplicate or missing page IDs.")
            }, ["coverage"]))
        };
        if (!referenceOnly)
        {
            result.Add(Define("save_paper_info", "Save the paper name, subject, and printed or fully derived total scores. User-edited values are preserved.", Object(new()
            {
                ["name"] = Describe(Nullable(String()), "Optional exam title visible on the paper, preserving its language, at most 240 characters. Use null or omit if no title is visible; never invent one or use a student name. The import filename remains the fallback."),
                ["subject"] = Describe(Nullable(String()), "Subject inferred from this paper, in Simplified Chinese; use a general-studies label for mixed subjects or null if unknown."),
                ["earned"] = Describe(Nullable(Number()), "Total earned points. Use null when unknown, when possible is unknown, or for blank papers. Cannot exceed possible."),
                ["possible"] = Describe(Nullable(Number()), "Total possible points, or null when unknown. Omitted score fields are treated as null.")
            }, ["subject"])));
            result.Add(Define("save_questions", "Save questions with their shared stems and gradable items, usually 4 to 8 questions per call. Use larger batches for short questions and smaller batches for long or complex questions. Returns an ordered result for each question; successful entries remain saved if another fails. Omitted auxiliary fields preserve saved values; explicit empty values replace them. Manual and teacher grades are protected.", Object(new()
            {
                ["questions"] = Describe(Array(QuestionSchema()), "Top-level questions to save in order.")
            })));
        }
        foreach (var name in new[] { "python", "shell" })
            result.Add(Define(name, name == "python"
                ? "Execute Python code in a shared notebook-style namespace for this task, including Pillow image processing and plotting. Imports, variables, functions, and images persist across Python calls and ordinary exceptions. A timeout, cancellation, output limit, or worker exit resets the namespace. Define variables before use; no crop helper is predefined. Files persist between calls. Returns status, stdout, stderr, and requested output images with reusable asset IDs. May require user approval."
                : "Execute self-contained Windows PowerShell code in a fresh process, including file and image processing. Files persist between calls; variables and functions do not. Returns status, stdout, stderr, and requested output images with reusable asset IDs. May require user approval.",
                Object(new()
                {
                    ["code"] = String("Complete executable code. Use relative paths in the supplied task working directory. Read page files from read_pages.local_file. Print textual results; save images to new files and list them in outputs."),
                    ["outputs"] = Describe(Array(String()), "Optional relative paths to generated PNG, JPEG, or WebP images to inspect and reuse. At most 8 images per call. Omit when no image output is needed."),
                    ["timeout_ms"] = Describe(new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 300000 }, "Optional timeout in milliseconds; defaults to 60000.")
                }, ["code"])));
        return result.Select(tool => tool with { Description = profile.DescribeTool(tool.Name, tool.Description) }).ToArray();
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
        ["page_id"] = String("Exact source page ID. Image and Mermaid content must use a material page."),
        ["bbox"] = Describe(new JsonObject
        {
            ["type"] = "array",
            ["minItems"] = 4,
            ["maxItems"] = 4,
            ["items"] = new JsonObject { ["type"] = "integer", ["minimum"] = 0, ["maximum"] = 999 }
        }, "[left,top,right,bottom] in integer coordinates normalized to 0..999. left < right and top < bottom. Full page: [0,0,999,999].")
    }), "Rectangle on the full unrotated source page, normalized to integer coordinates 0..999; never pixels, fractions, or relative to a crop or rotated display.");
    private static JsonObject Grade() => Object(new()
    {
        ["verdict"] = Describe(Enum("correct", "partial", "incorrect", "unanswered", "uncertain"), "correct=fully correct; partial=partly correct; incorrect=wrong; unanswered=visibly blank on an answered paper; uncertain=unreadable or insufficient evidence."),
        ["earned"] = Describe(Nullable(Number()), "Earned points, or null if unknown. Requires known possible. Correct equals possible; partial is strictly between zero and possible; incorrect/unanswered are zero; uncertain must be null."),
        ["possible"] = Describe(Nullable(Number()), "Maximum points, matching item.points. If not printed, a reasonable estimate based on common scoring conventions is allowed; record the allocation and basis in question.uncertainties. Use null if no reasonable estimate is available."),
        ["source"] = Describe(Enum("teacher", "ai_reference", "ai_solution", "manual"), "teacher for explicit teacher results; ai_reference for a verified matched reference; ai_solution otherwise. Never create a manual grade; preserve an existing user-edited one."),
        ["reason"] = String("Brief grading explanation in Simplified Chinese. Explain uncertainty; preserve teacher deduction text when a numerical score cannot be determined.")
    }, ["verdict", "source"]);
    private static JsonObject Reference() => Object(new()
    {
        ["key"] = String("Stable unique reference-answer key; reuse an existing key for the same answer."),
        ["number"] = String("Question or subquestion number as printed in the reference."),
        ["answer"] = String("Nonempty answer transcribed from the reference, preserving its language and LaTeX notation."),
        ["explanation"] = String("Explanation supported by the reference; omit when unavailable."),
        ["source_page_ids"] = Describe(Array(String()), "Source reference page IDs, including continuations."),
        ["matched_item_key"] = Describe(Nullable(String()), "Verified item key; null or omitted if unmatched. In reference-only tasks, match only existing saved item keys."),
        ["uncertain_reason"] = Describe(Nullable(String()), "Required nonempty reason when unmatched; null or omitted for a verified match.")
    }, ["key", "number", "answer", "source_page_ids"]);
    private static JsonObject Coverage() => Object(new()
    {
        ["page_id"] = String("Exact ID of a required page already read in full during this run."),
        ["status"] = Describe(Enum("covered", "unreadable", "no_questions"), "covered when all relevant content is accounted for; unreadable when relevant content cannot be read; no_questions only when there are no questions to extract."),
        ["question_keys"] = Describe(Array(String()), "For covered material pages, all saved question draft_key values whose sources include this page, including continuations. Use an empty list when no question keys apply."),
        ["reason"] = String("Nonempty explanation required for unreadable or no_questions; optional for covered.")
    }, ["page_id", "status"]);
    public static JsonObject QuestionSchema()
    {
        var text = Object(new() { ["kind"] = Describe(Enum("text"), "Text content block."), ["text"] = String("Transcribed question text in the source language; exclude student answers and teacher marks.") });
        var formula = Object(new() { ["kind"] = Describe(Enum("formula"), "Mathematical formula block."), ["latex"] = String("LaTeX formula wrapped in $...$.") });
        var table = Object(new() { ["kind"] = Describe(Enum("table"), "Structured table content block; prefer this or a Markdown table over a low-recognizability source crop."), ["rows"] = Describe(Array(Array(String())), "Rows in reading order; each row contains its cell texts in column order. To preserve merged cells exactly, use ^ for merge up, < for merge left, or > for merge right as the entire cell content.") });
        var image = Object(new()
        {
            ["kind"] = Describe(Enum("image"), "Diagram, figure, or content retained as an image."),
            ["region"] = Describe(Region(), "Source region for the diagram or unreadable content on a material page. Also identifies original evidence when asset_id is generated."),
            ["asset_id"] = String("Optional processed image asset ID returned by python or shell, or retained from this question. Displays the entire processed image; region still identifies original source evidence."),
            ["contains_handwriting"] = Describe(Bool(), "True if this image region contains handwriting."),
            ["caption"] = String("Brief description in Simplified Chinese; omit if unnecessary."),
            ["width_percent"] = Describe(new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100, ["default"] = 100 }, "Optional display width as a percentage of the available content width. 100 uses the full page content width; 50 uses half. Omit for 100. This does not change the source region.")
        }, ["kind", "region"]);
        var mermaid = Object(new()
        {
            ["kind"] = Describe(Enum("mermaid"), "Flowchart represented as Mermaid code rather than a source crop."),
            ["region"] = Describe(Region(), "Original material region containing the flowchart evidence."),
            ["code"] = Describe(new JsonObject { ["type"] = "string", ["minLength"] = 1, ["maxLength"] = 32768 }, "Nonempty raw Mermaid flowchart code, at most 32768 characters. Use only flowchart TD|LR|BT|RL or graph with simple nodes and labeled arrows; no fences, subgraphs, styling, callbacks, HTML, Markdown-in-node, or other diagram types. Transcribe only visible evidence."),
            ["caption"] = Describe(new JsonObject { ["type"] = "string", ["default"] = "" }, "Optional brief description in Simplified Chinese; defaults to an empty string."),
            ["width_percent"] = Describe(new JsonObject { ["type"] = "integer", ["minimum"] = 1, ["maximum"] = 100, ["default"] = 100 }, "Optional display width as a percentage of the available content width. 100 uses the full page content width; 50 uses half. Omit for 100.")
        }, ["kind", "region", "code"]);
        JsonObject Content(string description) => Describe(Array(new JsonObject { ["anyOf"] = new JsonArray(text.DeepClone(), formula.DeepClone(), table.DeepClone(), image.DeepClone(), mermaid.DeepClone()) }), description);
        var attempt = Object(new()
        {
            ["student_answer"] = String("Student response to this item only, preserving its language. Do not include teacher marks, reference answers, or AI solutions."),
            ["student_page_ids"] = Describe(Array(String()), "Optional source page IDs for this student response, from material or student pages. Match supplementary student pages to existing item keys; never create duplicate questions from them."),
            ["error_analysis"] = Describe(Nullable(String()), "For an incorrect or partially correct student response, explain the specific error and a useful correction in Simplified Chinese, grounded in visible work. Use null if the cause is unclear, the response is unreadable or blank, or the answer is correct. Do not guess a misconception from a wrong final answer alone."),
            ["teacher"] = Describe(Nullable(Object(new()
            {
                ["raw"] = String("Verbatim teacher comments or deduction marks for this item; do not invent missing text."),
                ["grade"] = Describe(Nullable(Grade()), "Explicit teacher result, with source=teacher. In teacher mode its verdict/scores/source must agree with attempt.grade. Omit or use null if unknown.")
            }, [])), "Teacher annotations, separate from the student response. Omit when unavailable; explicit null clears this field unless protected."),
            ["reference_key"] = Describe(Nullable(String()), "Key of a saved reference matched to this exact item key. Required when grading by a matched reference; null when none applies."),
            ["ai_solution"] = String("Legacy solution field; prefer item.answer and item.explanation for the standard answer and solution, including on blank papers. Omit to avoid repeating the same solution."),
            ["conflict"] = Describe(Nullable(String()), "Explain disagreement with the matched reference while still grading by that reference; null when there is no conflict."),
            ["grade"] = Describe(Grade(), "Verdict and scores for this item based on teacher priority, a matched reference, or your solution. Omission preserves the saved grade or defaults to uncertain.")
        }, ["error_analysis"]);
        var item = Object(new()
        {
            ["key"] = String("Nonempty stable item key, unique across all items of the paper, at most 128 UTF-8 bytes. Reuse existing keys on regrading. References match this key."),
            ["label"] = String("Printed subquestion label such as (1) or (2)(a). Required when the question has several items; use an empty string for a question without subquestions."),
            ["prompt"] = Content("Content specific to this subquestion in reading order. Leave empty for a question without subquestions, whose whole text belongs in stem."),
            ["options"] = Describe(Array(String()), "Answer options for this item in printed order, including their labels; omit if unavailable."),
            ["points"] = Describe(Nullable(Number()), "Maximum points for this item. Prefer printed values. If absent, estimate using common scoring conventions and question difficulty; item allocations must agree with any known question or paper total. Explain estimated allocations and their basis in question.uncertainties. Use null if no reasonable estimate is available. Question totals are derived from item points."),
            ["answer"] = String("Standard answer for this item, from a verified reference or your own solution. Generate it even on a blank paper. Use an empty string only when the answer cannot be determined and explain why in uncertainties."),
            ["explanation"] = String("Concise complete standard solution in Simplified Chinese, including the essential steps or justification. Generate it even on a blank paper, independently of student work. Do not repeat it in attempt.ai_solution."),
            ["attempt"] = Describe(Nullable(attempt), "Must be null on a blank paper. On answered papers, keep a nonnull attempt; distinguish visibly unanswered from unreadable/uncertain. Student answers, teacher marks, references, solutions, and grades belong inside attempt, never directly on the item. Omitted fields preserve existing information.")
        }, ["key", "answer", "explanation"]);
        return Object(new()
        {
            ["draft_key"] = String("Nonempty stable question key, unique across the paper, at most 128 UTF-8 bytes. Reuse existing keys on regrading. Coverage lists this key."),
            ["number"] = String("Nonempty top-level question number, preserving the printed label."),
            ["subject"] = String("Subject label in Simplified Chinese; omit if unknown."),
            ["question_type"] = String("Question type in Simplified Chinese; omit if unknown."),
            ["stem"] = Content("Nonempty shared question text in reading order: the material, context, diagrams, and instructions that precede or apply to all items. A question without subquestions puts its whole text here."),
            ["source_page_ids"] = Describe(Array(String()), "Source material page IDs, including every continuation page."),
            ["knowledge_points"] = Describe(Array(String()), "Relevant knowledge-point labels in Simplified Chinese; omit if unsupported."),
            ["uncertainties"] = Describe(Array(String()), "Specific unresolved transcription or interpretation issues in Simplified Chinese; retain image evidence instead of guessing."),
            ["items"] = Describe(Array(item), "Ordered gradable items, 1 to 100. Use exactly one item when the question has no subquestions. Otherwise use one item per answerable subquestion; flatten nested subquestions into separate items with combined labels such as (2)(a). Preserve protected items on regrading.")
        }, ["draft_key", "number", "stem", "source_page_ids", "items"]);
    }

    internal static JsonObject WithSourceRegions(JsonElement args, IReadOnlyList<Region>? previous)
    {
        var result = JsonNode.Parse(args.GetRawText()) as JsonObject ?? throw new JsonException("Expected an object.");
        GradingToolArguments.NormalizeQuestion(result);
        if (result["items"] is JsonArray items)
            foreach (var item in items.OfType<JsonObject>())
                if (item["attempt"] is JsonObject attempt && attempt.Remove("student_page_ids", out var studentPages))
                {
                    if (studentPages is not JsonArray answerPageIds) throw new JsonException("student_page_ids must be an array.");
                    var regions = new JsonArray();
                    var seenPages = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var page in answerPageIds)
                    {
                        string id = page?.GetValue<string>() ?? throw new JsonException("Student page IDs must be strings.");
                        if (string.IsNullOrWhiteSpace(id) || !seenPages.Add(id)) throw new JsonException("Student page IDs must be nonempty and unique.");
                        regions.Add(JsonNode.Parse(DomainJson.Serialize(new Region { PageId = id, X = 0, Y = 0, Width = 1, Height = 1 })));
                    }
                    attempt["student_regions"] = regions;
                }
        GradingCoordinates.ToStoredRegions(result);
        if (!result.Remove("source_page_ids", out var pages)) return result;
        if (result.ContainsKey("sources") || pages is not JsonArray { Count: > 0 } pageIds)
            throw new JsonException("Provide a nonempty source_page_ids array.");
        var sources = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in pageIds)
        {
            if (value is not JsonValue page || !page.TryGetValue<string>(out var id) || string.IsNullOrWhiteSpace(id) || !seen.Add(id))
                throw new JsonException("Source page IDs must be nonempty and unique.");
            var retained = previous?.Where(region => region.PageId == id).ToList() ?? [];
            if (retained.Count == 0) retained.Add(new Region { PageId = id, X = 0, Y = 0, Width = 1, Height = 1 });
            foreach (var region in retained) sources.Add(JsonNode.Parse(DomainJson.Serialize(region)));
        }
        result["sources"] = sources;
        return result;
    }

    internal static void UsePageSources(JsonNode? node)
    {
        if (node is JsonArray array) { foreach (var item in array) UsePageSources(item); }
        if (node is not JsonObject obj) return;
        if (obj.Remove("sources", out var sources) && sources is JsonArray regions)
            obj["source_page_ids"] = new JsonArray(regions.Select(region => region!["page_id"]!.GetValue<string>())
                .Distinct(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        if (obj.Remove("student_regions", out var studentRegions) && studentRegions is JsonArray studentPages)
            obj["student_page_ids"] = new JsonArray(studentPages.Select(region => region!["page_id"]!.GetValue<string>()).Distinct(StringComparer.Ordinal).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
        obj.Remove("student_answer_edited");
        obj.Remove("regions");
        if (obj["region"] is JsonObject region) obj["region"] = GradingCoordinates.ToModelRegion(DomainJson.Deserialize<Region>(region.ToJsonString()));
        foreach (var property in obj) UsePageSources(property.Value);
    }

    internal static void Fields(JsonElement args, params string[] allowed)
    {
        if (args.ValueKind != JsonValueKind.Object || args.EnumerateObject().Any(p => !allowed.Contains(p.Name, StringComparer.Ordinal))) throw new JsonException("Tool arguments do not match the definition.");
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(JsonObject))]
internal sealed partial class ProviderSchemaJson : System.Text.Json.Serialization.JsonSerializerContext;
