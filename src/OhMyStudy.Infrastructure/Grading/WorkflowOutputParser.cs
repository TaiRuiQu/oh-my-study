using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

internal sealed record WorkflowParsedOutput<T>(T Value, string Json, List<string> Changes);

internal static class WorkflowOutputParser
{
    private static readonly JsonDocumentOptions ReadOptions = new() { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip, MaxDepth = 256 };
    private static readonly string[] Wrappers = ["result", "data", "output", "response", "plan", "split_plan", "layout", "classification", "solution", "assessment", "content"];

    public static WorkflowParsedOutput<T> Parse<T>(string text, JsonNode? input = null)
    {
        var changes = new List<string>();
        var node = Read(text, changes);
        var schema = WorkflowOutputSchema.Schema<T>(input);
        for (int depth = 0; depth < 5; depth++)
        {
            if (node is JsonValue value && value.TryGetValue<string>(out string? encoded))
            {
                node = Read(encoded, changes);
                changes.Add("Decoded a JSON string envelope.");
                continue;
            }
            if (node is not JsonObject envelope || HasRootFields<T>(envelope)) break;
            var wrapped = Wrappers.Select(name => Find(envelope, name).Value).FirstOrDefault(value => value is JsonObject or JsonArray
                || value is JsonValue scalar && scalar.TryGetValue<string>(out string? content) && content.TrimStart().StartsWith('{'));
            if (wrapped is null) break;
            node = wrapped.DeepClone();
            changes.Add("Removed a result envelope.");
        }
        string collection = typeof(T) == typeof(ExamLayout) ? "pages" : typeof(T) == typeof(QuestionSplitPlan)
            ? Flag(input?["reference_only"]) ? "references" : "questions" : "items";
        if (node is JsonArray) node = new JsonObject { [collection] = node };
        if (node is not JsonObject root) throw new JsonException("The workflow result must contain a JSON object or item list.");
        if (typeof(T) == typeof(QuestionAssessment)) PrepareAssessments(root, changes);
        if (typeof(T) == typeof(QuestionSolution) && Find(root, "items").Value is null && Find(root, "answer").Value is not null)
            root = new JsonObject { ["items"] = new JsonArray(root) };
        bool referencesRequired = Flag(input?["reference_only"])
            || (input?["page_order"] as JsonArray ?? []).OfType<JsonObject>().Any(page => page["role"]?.ToString() == "reference");
        if (typeof(T) == typeof(QuestionSplitPlan) && referencesRequired && Find(root, "references").Value is null)
            throw new JsonException("Reference pages require an explicit references list.");
        var original = root.DeepClone();
        var normalized = Normalize(root, schema, "$", "", changes)?.AsObject() ?? throw new JsonException("The workflow result is empty.");
        Complete<T>(normalized, input, changes);
        if (!JsonNode.DeepEquals(original, normalized)) changes.Add("Normalized workflow structure and defaults.");
        Validate(normalized, schema, "$");
        string json = normalized.ToJsonString();
        return new(DomainJson.Deserialize<T>(json), json, changes.Distinct(StringComparer.Ordinal).Take(256).ToList());
    }

    private static bool HasRootFields<T>(JsonObject obj) => typeof(T) == typeof(ExamLayout)
        ? Find(obj, "pages").Value is not null || Find(obj, "format").Value is JsonValue
        : typeof(T) == typeof(QuestionSplitPlan) ? Find(obj, "questions").Value is not null
        : Find(obj, "items").Value is not null || typeof(T) == typeof(QuestionSolution) && Find(obj, "answer").Value is JsonValue
            || typeof(T) == typeof(QuestionAssessment) && (obj.Any(p => Id(p.Key) is "attempt" or "studentanswer" or "verdict")
                || Find(obj, "grade").Value is JsonObject || Find(obj, "earned").Value is not null);

    private static JsonNode Read(string text, List<string> changes)
    {
        string source = text.Trim().Trim('\uFEFF'), fence = new((char)96, 3);
        if (source.StartsWith(fence, StringComparison.Ordinal))
        {
            int first = source.IndexOf('\n'), last = source.LastIndexOf(fence, StringComparison.Ordinal);
            if (first >= 0 && last > first) source = source[(first + 1)..last].Trim();
            changes.Add("Removed Markdown fences.");
        }
        try { return JsonNode.Parse(source, documentOptions: ReadOptions) ?? throw new JsonException("Empty JSON."); }
        catch (JsonException)
        {
            string repaired = EscapeInvalidBackslashes(source);
            if (repaired != source)
                try
                {
                    var parsed = JsonNode.Parse(repaired, documentOptions: ReadOptions);
                    if (parsed is not null) { changes.Add("Escaped invalid string escapes."); return parsed; }
                }
                catch (JsonException) { }
            for (int start = 0; start < source.Length; start++)
            {
                if (source[start] is not ('{' or '[')) continue;
                int depth = 0; bool quoted = false, escaped = false, completed = false;
                for (int end = start; end < source.Length; end++)
                {
                    char c = source[end];
                    if (quoted)
                    {
                        if (escaped) escaped = false;
                        else if (c == '\\') escaped = true;
                        else if (c == '"') quoted = false;
                        continue;
                    }
                    if (c == '"') { quoted = true; continue; }
                    if (c is '{' or '[') depth++;
                    if (c is not ('}' or ']') || --depth != 0) continue;
                    completed = true;
                    try
                    {
                        var parsed = JsonNode.Parse(EscapeInvalidBackslashes(source[start..(end + 1)]), documentOptions: ReadOptions);
                        if (parsed is not null) { changes.Add("Extracted JSON from surrounding text."); return parsed; }
                    }
                    catch (JsonException) { }
                    start = end;
                    break;
                }
                if (!completed) break;
            }
            throw;
        }
    }

    private static string EscapeInvalidBackslashes(string source)
    {
        var result = new StringBuilder(source.Length);
        bool quoted = false;
        for (int i = 0; i < source.Length; i++)
        {
            char c = source[i];
            if (c == '"') quoted = !quoted;
            if (quoted && c == '\\' && i + 1 < source.Length)
            {
                char next = source[i + 1];
                bool unicode = next == 'u' && i + 5 < source.Length && source.AsSpan(i + 2, 4).ToString().All(Uri.IsHexDigit);
                if ("\"\\/bfnrt".Contains(next) || unicode) { result.Append(c).Append(next); i++; continue; }
                result.Append('\\');
            }
            result.Append(c);
        }
        return result.ToString();
    }

    private static JsonObject Shape(JsonObject schema) => schema["anyOf"] is JsonArray choices
        ? choices.OfType<JsonObject>().FirstOrDefault(choice => choice["type"]?.ToString() != "null") ?? schema : schema;
    private static bool Nullable(JsonObject schema) => schema["type"] is JsonArray types && types.Any(t => t?.ToString() == "null")
        || schema["anyOf"] is JsonArray choices && choices.OfType<JsonObject>().Any(choice => choice["type"]?.ToString() == "null");

    private static JsonNode? Normalize(JsonNode? node, JsonObject contract, string path, string name, List<string> changes)
    {
        var schema = Shape(contract);
        if (node is null)
        {
            if (Nullable(contract)) return null;
            if (schema["items"] is not null && name is not ("questions" or "pages" or "items" or "coverage")) return new JsonArray();
            if (name is "reason" or "note" or "subject" or "question_type" or "label" or "prompt" or "explanation" or "student_answer" or "ai_solution" or "raw") return JsonValue.Create("");
            return null;
        }
        if (schema["properties"] is JsonObject properties)
        {
            if (node is JsonValue encoded && encoded.TryGetValue<string>(out string? json))
            {
                if (Nullable(contract) && (string.IsNullOrWhiteSpace(json) || Id(json) is "null" or "none")) return null;
                if (properties.ContainsKey("raw") && properties.ContainsKey("regions"))
                { node = new JsonObject { ["raw"] = json, ["regions"] = new JsonArray(), ["grade"] = null }; changes.Add("Preserved a text teacher annotation."); }
                else if (properties.ContainsKey("verdict") && json.Contains('/'))
                    node = new JsonObject { ["earned"] = json };
                else node = Read(json, changes);
            }
            if (node is not JsonObject source) throw new JsonException("Expected an object at " + path);
            source = source.DeepClone().AsObject();
            if (properties.ContainsKey("page_id") && properties.ContainsKey("width")) Region(source, changes);
            var suppliedAttempt = Find(source, "attempt").Value;
            bool objectAttempt = suppliedAttempt is JsonObject || suppliedAttempt is JsonValue encodedAttempt
                && encodedAttempt.TryGetValue<string>(out string? attemptText) && attemptText.TrimStart().StartsWith('{');
            if (properties.ContainsKey("key") && properties.ContainsKey("attempt") && !objectAttempt
                && (Find(source, "grade").Value is not null || Find(source, "student_answer").Value is not null
                    || Find(source, "verdict").Value is not null || Find(source, "earned").Value is not null))
            {
                var attempt = source.DeepClone().AsObject();
                if (suppliedAttempt is JsonValue answer && answer.TryGetValue<string>(out string? response)) attempt["student_answer"] ??= JsonValue.Create(response);
                source["attempt"] = attempt;
                changes.Add("Nested a flat assessment at " + path);
            }
            if (properties.ContainsKey("student_answer") && Find(source, "grade").Value is null)
            {
                var grade = source["score"] is JsonObject scoreObject ? scoreObject.DeepClone().AsObject() : new JsonObject();
                foreach (string field in new[] { "verdict", "earned", "possible", "source", "reason" })
                    if (Find(source, field).Value is { } fieldValue) grade[field] = fieldValue.DeepClone();
                if (grade.Count > 0) { source["grade"] = grade; changes.Add("Nested a flat grade at " + path); }
            }
            if (properties.ContainsKey("verdict"))
            {
                if (source["score"] is JsonObject scoreObject)
                    foreach (string field in new[] { "verdict", "earned", "possible", "source", "reason" })
                        if (Find(scoreObject, field).Value is { } scoreValue && (Find(source, field).Value is null || field == "earned" && Find(source, field).Value is JsonObject))
                            source[field] = scoreValue.DeepClone();
                Fraction(source, changes);
            }
            var result = new JsonObject();
            var consumed = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in properties)
            {
                var field = Find(source, property.Key);
                var childSchema = property.Value!.AsObject();
                if (field.Name is not null)
                {
                    consumed.Add(field.Name);
                    result[property.Key] = Normalize(field.Value, childSchema, path + "." + property.Key, property.Key, changes);
                    if (field.Name != property.Key) changes.Add("Renamed " + path + "." + field.Name + " to " + property.Key);
                }
                else if (Nullable(childSchema)) result[property.Key] = null;
                else if (Shape(childSchema)["type"]?.ToString() == "array" && property.Key is not ("questions" or "pages" or "items" or "coverage"))
                    result[property.Key] = new JsonArray();
                else if (property.Key is "reason" or "note" or "subject" or "question_type" or "label" or "prompt" or "explanation" or "student_answer" or "ai_solution" or "raw")
                    result[property.Key] = "";
                else if (property.Key == "student_answer_edited") result[property.Key] = false;
            }
            foreach (var field in source.Where(p => !consumed.Contains(p.Key))) changes.Add("Ignored extra field " + path + "." + field.Key);
            return result;
        }
        if (schema["items"] is JsonObject element)
        {
            if (node is JsonValue scalar && scalar.TryGetValue<string>(out string? text))
            {
                if (text.TrimStart().StartsWith('[')) node = Read(text, changes);
                else if (name.EndsWith("_ids", StringComparison.Ordinal))
                    node = new JsonArray(text.Split([',', ';', '\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(id => (JsonNode?)JsonValue.Create(id)).ToArray());
            }
            if (name == "options" && node is JsonObject answerOptions)
            {
                node = new JsonArray(answerOptions.Select(option => (JsonNode?)JsonValue.Create(option.Key + ". " + (option.Value is null ? "" : Text(option.Value)))).ToArray());
                changes.Add("Preserved labeled answer options at " + path);
            }
            if (node is JsonObject map && Shape(element)["properties"] is JsonObject itemProperties)
            {
                string? key = new[] { "key", "item_key", "page_id" }.FirstOrDefault(itemProperties.ContainsKey);
                if (key is not null && !map.Any(pair => itemProperties.Any(property => Id(pair.Key) == Id(property.Key))))
                {
                    var values = new JsonArray();
                    foreach (var pair in map)
                    {
                        var item = pair.Value is JsonObject mapped ? mapped.DeepClone().AsObject() : new JsonObject { ["answer"] = pair.Value?.DeepClone() };
                        if (Find(item, key).Value is null) item[key] = pair.Key;
                        values.Add((JsonNode)item);
                    }
                    node = values;
                    changes.Add("Converted a keyed object to an array at " + path);
                }
            }
            if (node is not JsonArray array) { array = new JsonArray(node.DeepClone()); changes.Add("Wrapped a single value at " + path); }
            if (name == "answer_bindings")
            {
                var expanded = new JsonArray();
                foreach (var item in array)
                {
                    if (item is JsonObject binding && binding["item_keys"] is JsonArray keys)
                    {
                        foreach (var key in keys)
                        {
                            var copy = binding.DeepClone().AsObject();
                            copy.Remove("item_keys");
                            copy["item_key"] = key?.DeepClone();
                            expanded.Add((JsonNode)copy);
                        }
                        changes.Add("Expanded explicitly shared answer bindings at " + path);
                    }
                    else expanded.Add(item?.DeepClone());
                }
                array = expanded;
            }
            return new JsonArray(array.Where(item => item is not null).Select((item, index) =>
            {
                if (name.EndsWith("_ids", StringComparison.Ordinal) && item is JsonObject identifier)
                {
                    var id = Find(identifier, "id").Value ?? Find(identifier, "key").Value
                        ?? identifier["block_id"] ?? identifier["detection_id"];
                    if (id is null) throw new JsonException("An evidence identifier is missing at " + path + "[" + index + "]");
                    item = id;
                    changes.Add("Extracted an evidence identifier at " + path);
                }
                return Normalize(item, element, path + "[" + index + "]", name, changes);
            }).ToArray());
        }
        string? type = schema["type"] is JsonArray alternatives ? alternatives.FirstOrDefault(t => t?.ToString() != "null")?.ToString() : schema["type"]?.ToString();
        if (node is JsonObject scalarObject && scalarObject["value"] is JsonValue scalarValue)
        { node = scalarValue.DeepClone(); changes.Add("Unwrapped a scalar value at " + path); }
        if (node is not JsonValue && type == "string")
        { node = JsonValue.Create(Text(node))!; changes.Add("Joined text content at " + path); }
        if (node is not JsonValue valueNode) return node.DeepClone();
        string? valueText = valueNode.TryGetValue<string>(out string? textValue) ? textValue.Trim() : null;
        if ((type is "number" or "integer") && valueText is not null)
        {
            if (Nullable(contract) && (valueText.Length == 0 || Id(valueText) is "null" or "none" or "unknown")) return null;
            bool percent = valueText.EndsWith('%') && (name is "confidence" or "x" or "y" or "width" or "height");
            string number = valueText.TrimEnd('分').Trim();
            if (percent) number = number.TrimEnd('%');
            foreach (string unit in new[] { "points", "point", "pts", "pt" })
                if (number.EndsWith(unit, StringComparison.OrdinalIgnoreCase)) { number = number[..^unit.Length].Trim(); break; }
            if (double.TryParse(number, NumberStyles.Float, CultureInfo.InvariantCulture, out double numeric) && double.IsFinite(numeric))
            { changes.Add("Read a numeric string at " + path); return JsonValue.Create(percent ? numeric / 100 : numeric); }
        }
        if (type == "boolean")
        {
            string boolean = (valueText ?? node.ToJsonString()).ToLowerInvariant();
            if (boolean is "true" or "yes" or "1") { if (valueText is not null || boolean == "1") changes.Add("Read a Boolean value at " + path); return JsonValue.Create(true); }
            if (boolean is "false" or "no" or "0") { if (valueText is not null || boolean == "0") changes.Add("Read a Boolean value at " + path); return JsonValue.Create(false); }
        }
        if (schema["enum"] is JsonArray options)
        {
            string candidate = valueText ?? node.ToJsonString();
            var match = options.FirstOrDefault(option => Id(option!.GetValue<string>()) == Id(candidate))
                ?? options.FirstOrDefault(option => Id(option!.GetValue<string>()) == Id(EnumAlias(candidate)));
            if (match is null && Id(candidate) is "unreadable" or "unknown")
                match = options.FirstOrDefault(option => option?.ToString() == "unmatched")
                    ?? options.FirstOrDefault(option => option?.ToString() == "unreadable");
            if (match is null && Id(candidate) is "complete" or "completed" or "ok")
                match = options.FirstOrDefault(option => option?.ToString() == "covered");
            if (match is null && Id(candidate) == "empty")
                match = options.FirstOrDefault(option => option?.ToString() == "no_questions");
            if (match is null && Id(candidate) == "blank")
                match = options.FirstOrDefault(option => option?.ToString() == "unanswered");
            if (match is not null) { if (match.ToString() != valueText) changes.Add("Normalized an enum at " + path); return match.DeepClone(); }
        }
        if (type == "string" && valueText is null) return JsonValue.Create(node.ToJsonString());
        return node.DeepClone();
    }

    private static void PrepareAssessments(JsonObject root, List<string> changes)
    {
        var items = Find(root, "items").Value;
        if (items is null && (Find(root, "attempt").Value is not null || Find(root, "grade").Value is not null
            || Find(root, "verdict").Value is not null || Find(root, "earned").Value is not null))
        {
            var copy = root.DeepClone();
            root.Clear();
            root["items"] = new JsonArray(copy);
            changes.Add("Wrapped a flat assessment result.");
        }
    }

    private static void Complete<T>(JsonObject root, JsonNode? input, List<string> changes)
    {
        if (typeof(T) == typeof(ExamLayout))
        {
            root["format"] ??= JsonValue.Create("uncertain");
            root["confidence"] ??= JsonValue.Create(0d);
            string format = root["format"]!.ToString();
            foreach (var page in (root["pages"] as JsonArray ?? []).OfType<JsonObject>())
            {
                page["has_teacher_marks"] ??= JsonValue.Create(format is "marked_inline_answers" or "marked_answer_sheet" && page["kind"]?.ToString() is "answer_sheet" or "mixed");
                page["has_student_work"] ??= JsonValue.Create(format != "blank_paper" && page["kind"]?.ToString() is "answer_sheet" or "mixed" or "uncertain");
            }
        }
        if (typeof(T) == typeof(QuestionSplitPlan))
        {
            if (Flag(input?["reference_only"])) root["questions"] ??= new JsonArray();
            bool blank = input?["mode"]?.ToString() == "blank";
            foreach (var question in (root["questions"] as JsonArray ?? []).OfType<JsonObject>())
            {
                var items = (question["items"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
                foreach (var binding in (question["answer_bindings"] as JsonArray ?? []).OfType<JsonObject>())
                {
                    string key = binding["item_key"]?.ToString() ?? "";
                    var matches = items.Where(item => Id(item["key"]?.ToString() ?? "") == Id(key)
                        || !string.IsNullOrWhiteSpace(item["label"]?.ToString()) && Id(item["label"]!.ToString()) == Id(key)).ToArray();
                    if (matches.Length == 1 && key != matches[0]["key"]?.ToString())
                    { binding["item_key"] = matches[0]["key"]?.DeepClone(); changes.Add("Restored a supplied answer-binding item key."); }
                }
                if (question["answer_bindings"] is JsonArray sharedBindings && sharedBindings.Count == 1
                    && sharedBindings[0] is JsonObject shared && items.Length > 0
                    && !string.IsNullOrWhiteSpace(question["key"]?.ToString())
                    && SharedBinding(question, items, shared))
                {
                    question["answer_bindings"] = new JsonArray(items.Select(item =>
                    {
                        var binding = shared.DeepClone().AsObject();
                        binding["item_key"] = item["key"]?.DeepClone();
                        return (JsonNode)binding;
                    }).ToArray());
                    changes.Add("Expanded a shared question response region for " + question["key"]);
                }
                var bindings = (question["answer_bindings"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
                foreach (var binding in bindings)
                {
                    bool evidence = (binding["block_ids"] as JsonArray)?.Count > 0 || (binding["regions"] as JsonArray)?.Count > 0;
                    binding["status"] ??= JsonValue.Create(blank ? "none" : evidence ? "matched" : "unmatched");
                    if (binding["status"]?.ToString() == "unmatched" && string.IsNullOrWhiteSpace(binding["reason"]?.ToString()))
                        binding["reason"] = "The response location was not supplied.";
                }
                question["has_student_work"] ??= JsonValue.Create(!blank && bindings.Any(binding => binding["status"]?.ToString() != "none"));
            }
        }
        if (typeof(T) != typeof(QuestionAssessment) && typeof(T) != typeof(QuestionSolution)) return;
        var expected = (input?["question"]?["items"] as JsonArray ?? []).OfType<JsonObject>().ToArray();
        foreach (var item in (root["items"] as JsonArray ?? []).OfType<JsonObject>())
        {
            string? key = item["key"]?.ToString();
            var matching = expected.Where(value => Id(value["key"]?.ToString() ?? "") == Id(key ?? "")
                || !string.IsNullOrWhiteSpace(key) && !string.IsNullOrWhiteSpace(value["label"]?.ToString()) && Id(value["label"]!.ToString()) == Id(key)).ToArray();
            bool parentKey = !string.IsNullOrWhiteSpace(key) && new[] { "number", "draft_key", "key" }.Any(field => Id(input?["question"]?[field]?.ToString() ?? "") == Id(key));
            var original = matching.Length == 1 ? matching[0] : (string.IsNullOrWhiteSpace(key) || parentKey) && expected.Length == 1 ? expected[0] : null;
            if (original is not null && key != original["key"]?.ToString())
            { item["key"] = original["key"]?.DeepClone(); changes.Add("Restored the supplied item key."); }
            if (item["attempt"] is not JsonObject attempt || attempt["grade"] is not JsonObject grade) continue;
            string actualKey = item["key"]?.ToString() ?? "";
            var reference = (input?["references"] as JsonArray ?? []).OfType<JsonObject>().FirstOrDefault(value => value["matched_item_key"]?.ToString() == actualKey);
            if (reference is not null) attempt["reference_key"] ??= reference["key"]?.DeepClone();
            string source = reference is null ? "ai_solution" : "ai_reference";
            if (attempt["teacher"] is JsonObject teacher && teacher["grade"] is JsonObject teacherGrade)
            {
                CompleteGrade(teacherGrade, original?["points"], "teacher", changes);
                if (input?["mode"]?.ToString() is "teacher_graded" or "auto") source = "teacher";
            }
            CompleteGrade(grade, original?["points"], source, changes);
        }
    }

    private static bool SharedBinding(JsonObject question, JsonObject[] items, JsonObject binding)
    {
        string parent = Id(question["key"]!.ToString()), key = Id(binding["item_key"]?.ToString() ?? "");
        bool parentAlias = new[] { "ans", "answer", "answers", "response", "responses" }.Any(suffix => key == parent + suffix);
        bool parentOnly = key == parent && !items.Any(item => Id(item["key"]?.ToString() ?? "") == key);
        bool evidence = binding["block_ids"] is JsonArray { Count: > 0 } || binding["regions"] is JsonArray { Count: > 0 };
        if ((parentAlias || parentOnly) && (evidence || binding["status"]?.ToString() is "unmatched" or "none")) return true;
        return key == parent && question["student_regions"] is JsonArray { Count: > 0 } regions && JsonNode.DeepEquals(binding["regions"], regions);
    }

    private static void CompleteGrade(JsonObject grade, JsonNode? points, string source, List<string> changes)
    {
        grade["source"] ??= JsonValue.Create(source);
        grade["possible"] ??= points?.DeepClone();
        if (grade["earned"] is JsonValue score && score.TryGetValue<string>(out string? percentage) && percentage.EndsWith('%')
            && Number(grade["possible"]) is { } maximum && double.TryParse(percentage.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out double percent))
        { grade["earned"] = maximum * percent / 100; changes.Add("Converted a percentage score."); }
        double? earned = Number(grade["earned"]), possible = Number(grade["possible"]);
        grade["verdict"] ??= JsonValue.Create(earned is null || possible is null ? "uncertain" : earned == possible ? "correct" : earned == 0 ? "incorrect" : "partial");
        if (grade["earned"] is null)
            grade["earned"] = grade["verdict"]?.ToString() == "correct" ? grade["possible"]?.DeepClone()
                : (grade["verdict"]?.ToString() is "incorrect" or "unanswered") && possible is not null ? JsonValue.Create(0d) : null;
    }

    private static void Region(JsonObject region, List<string> changes)
    {
        double? left = Number(Find(region, "x").Value), top = Number(Find(region, "y").Value);
        double? right = Number(Find(region, "right").Value), bottom = Number(Find(region, "bottom").Value);
        if (Find(region, "bbox").Value is JsonArray box && box.Count == 4 && box.All(value => Number(value) is >= 0 and <= 1))
        {
            left = Number(box[0]); top = Number(box[1]); right = Number(box[2]); bottom = Number(box[3]);
            if (Id(region["bbox_format"]?.ToString() ?? region["format"]?.ToString() ?? "") == "xywh") { right += left; bottom += top; }
        }
        if (left is { } x && top is { } y && right is { } r && bottom is { } b && Find(region, "width").Value is null)
        {
            region["x"] = x; region["y"] = y; region["width"] = r - x; region["height"] = b - y;
            changes.Add("Converted source-region corners to width and height.");
        }
    }

    private static void Fraction(JsonObject grade, List<string> changes)
    {
        var score = Find(grade, "earned").Value;
        if (score is not JsonValue value || !value.TryGetValue<string>(out string? text)) return;
        var parts = text.Split('/');
        if (parts.Length == 2 && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out double earned)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double possible))
        {
            grade["earned"] = earned;
            if (Find(grade, "possible").Value is null) grade["possible"] = possible;
            changes.Add("Separated an earned/maximum score.");
        }
    }

    private static double? Number(JsonNode? node)
    {
        string? text = node is JsonValue scalar && scalar.TryGetValue<string>(out string? value) ? value : node?.ToJsonString();
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double number) && double.IsFinite(number) ? number : null;
    }
    private static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out bool flag) && flag;
    private static string Id(string value) => string.Concat(value.Where(char.IsLetterOrDigit)).ToLowerInvariant();

    private static string Text(JsonNode node)
    {
        if (node is JsonValue value) return value.TryGetValue<string>(out string? text) ? text : node.ToJsonString();
        if (node is JsonArray array) return string.Join("\n", array.Where(value => value is not null).Select(value => Text(value!)));
        if (node is JsonObject obj)
        {
            foreach (string field in new[] { "text", "markdown", "content", "value", "answer", "key", "id" })
                if (obj[field] is { } content) return Text(content);
            if (obj["latex"] is { } latex) return "$" + Text(latex) + "$";
        }
        return node.ToJsonString();
    }

    private static void Validate(JsonNode? node, JsonObject schema, string path)
    {
        if (schema["anyOf"] is JsonArray choices)
        {
            var matching = choices.OfType<JsonObject>().FirstOrDefault(choice => Matches(node, choice));
            if (matching is null) throw new JsonException("Invalid value type at " + path);
            Validate(node, matching, path);
            return;
        }
        if (!Matches(node, schema)) throw new JsonException("Invalid value type at " + path);
        if (node is null) return;
        if (schema["enum"] is JsonArray options && !options.Any(value => JsonNode.DeepEquals(value, node)))
            throw new JsonException("Invalid enum value at " + path + ": " + node.ToJsonString());
        if (node is JsonObject obj && schema["properties"] is JsonObject properties)
        {
            if (schema["required"] is JsonArray required)
                foreach (var name in required)
                    if (!obj.ContainsKey(name!.GetValue<string>())) throw new JsonException("Missing required field " + path + "." + name);
            foreach (var property in obj)
                if (properties[property.Key] is JsonObject child) Validate(property.Value, child, path + "." + property.Key);
                else throw new JsonException("Unexpected field " + path + "." + property.Key);
        }
        if (node is JsonArray array && schema["items"] is JsonObject element)
            for (int i = 0; i < array.Count; i++) Validate(array[i], element, path + "[" + i + "]");
    }

    private static bool Matches(JsonNode? node, JsonObject schema)
    {
        var types = schema["type"] is JsonArray array ? array.Select(value => value!.GetValue<string>()).ToArray()
            : schema["type"] is JsonValue value ? [value.GetValue<string>()] : Array.Empty<string>();
        if (types.Length == 0) return true;
        return types.Any(type => type switch
        {
            "null" => node is null,
            "object" => node is JsonObject,
            "array" => node is JsonArray,
            "string" => node is JsonValue scalar && scalar.TryGetValue<string>(out _),
            "boolean" => node is JsonValue scalar && scalar.TryGetValue<bool>(out _),
            "number" => node?.GetValueKind() == JsonValueKind.Number && Number(node) is not null,
            "integer" => node?.GetValueKind() == JsonValueKind.Number && Number(node) is { } number && number % 1 == 0,
            _ => false
        });
    }

    private static (string? Name, JsonNode? Value) Find(JsonObject obj, string name)
    {
        var exact = obj.Where(pair => Id(pair.Key) == Id(name)).ToArray();
        if (exact.Length > 1 && exact.Skip(1).Any(pair => !JsonNode.DeepEquals(pair.Value, exact[0].Value))) throw new JsonException("Conflicting values for " + name);
        if (exact.Length > 0) return (exact[0].Key, exact[0].Value);
        foreach (string alias in Aliases(name))
            foreach (var pair in obj)
                if (Id(pair.Key) == Id(alias)) return (pair.Key, pair.Value);
        return (null, null);
    }

    private static string[] Aliases(string name) => name switch
    {
        "items" => ["results", "answers", "solutions", "assessments", "parts", "subquestions", "scoring_items"],
        "questions" => ["split_questions", "question_list"],
        "references" => ["reference_answers", "standard_answers", "official_answers"],
        "coverage" => ["page_coverage", "pages_coverage", "pages"],
        "pages" => ["page_layouts", "page_classifications"],
        "key" => ["item_key", "item_id", "id", "draft_key"],
        "item_key" => ["key", "item_id", "id"],
        "page_id" => ["page", "page_key", "source_page_id", "id"],
        "number" => ["question_number", "question_no", "no"],
        "label" => ["item_label", "part", "subquestion_number", "number"],
        "format" => ["exam_format", "exam_type", "layout"],
        "kind" => ["page_kind", "page_type", "role", "type"],
        "confidence" => ["confidence_score"],
        "has_student_work" => ["has_answers", "has_responses", "has_handwriting"],
        "has_teacher_marks" => ["has_teacher_marking", "teacher_marked", "has_grading_marks"],
        "markdown" => ["stem", "question_text", "text", "content"],
        "answer_bindings" => ["bindings", "response_bindings", "answer_binding", "response_binding"],
        "block_ids" => ["ocr_block_ids", "ocr_ids", "blocks"],
        "detection_ids" => ["region_ids", "detected_region_ids", "detections"],
        "ignored_block_ids" => ["ignored_ocr_block_ids", "unused_block_ids", "ignored_blocks"],
        "ignored_detection_ids" => ["ignored_region_ids", "unused_detection_ids"],
        "student_regions" => ["answer_regions", "response_regions"],
        "regions" => ["source_regions", "bounds"],
        "sources" => ["source_regions", "regions", "evidence_regions"],
        "question_keys" => ["question_ids", "keys"],
        "points" => ["max_points", "maximum_points", "full_marks"],
        "attempt" => ["assessment", "response", "result"],
        "grade" => ["grading", "evaluation"],
        "student_answer" => ["response_text", "student_response"],
        "answer" => ["solution", "final_answer"],
        "explanation" => ["reasoning", "analysis", "solution_explanation"],
        "earned" => ["score", "earned_points", "awarded_points"],
        "possible" => ["max_score", "max_points", "maximum", "full_marks", "total_points"],
        "verdict" => ["judgment", "judgement", "correctness", "result", "status"],
        "status" => ["state", "binding_status", "coverage_status", "match_status"],
        "source" => ["grade_source"],
        "reference_key" => ["reference_id", "reference_answer_key"],
        "matched_item_key" => ["matched_item_id", "matched_key", "item_key", "item_id"],
        "uncertain_reason" => ["unmatched_reason", "uncertainty_reason", "reason"],
        "teacher" => ["teacher_marks", "teacher_annotation", "teacher_mark"],
        "error_analysis" => ["error_reason", "mistake_analysis"],
        "ai_solution" => ["model_solution", "standard_solution"],
        "reason" => ["message", "comment", "rationale"],
        "note" => ["notes", "description"],
        "knowledge_points" => ["topics", "knowledge"],
        "uncertainties" => ["warnings", "uncertainty", "issues"],
        "x" => ["left", "x1"],
        "y" => ["top", "y1"],
        "right" => ["x2"],
        "bottom" => ["y2"],
        "width" => ["w"],
        "height" => ["h"],
        "bbox" => ["bounding_box"],
        _ => []
    };

    private static string EnumAlias(string value) => Id(value) switch
    {
        "material" or "question" or "paper" or "试题页" => "question_paper",
        "student" or "answers" or "答题卡" => "answer_sheet",
        "referencepage" or "referenceanswers" => "reference",
        "blank" or "blankexam" => "blank_paper",
        "separate" or "separateanswers" => "separate_answer_sheet",
        "unknown" or "unreadable" or "unclear" or "待核对" => "uncertain",
        "right" or "true" or "正确" => "correct",
        "wrong" or "false" or "错误" => "incorrect",
        "partiallycorrect" or "partlycorrect" or "部分正确" => "partial",
        "notanswered" or "未作答" => "unanswered",
        "bound" or "linked" => "matched",
        "missing" or "ambiguous" => "unmatched",
        "empty" => "blank",
        "notapplicable" or "na" => "none",
        "ai" or "aisolved" => "ai_solution",
        "referenceanswer" => "ai_reference",
        "teachergraded" => "teacher",
        _ => value
    };
}
