using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Providers;

/// <summary>Native protocol framing. Opaque context is copied without interpreting signatures.</summary>
public static class ProviderProtocol
{
    public static JsonArray Initial(Protocol protocol, string text, IReadOnlyList<Image> images)
    {
        var blocks = new JsonArray();
        blocks.Add((JsonNode)(protocol switch
        {
            Protocol.Responses => new JsonObject { ["type"] = "input_text", ["text"] = text },
            Protocol.Messages => new JsonObject { ["type"] = "text", ["text"] = text },
            _ => new JsonObject { ["text"] = text }
        }));
        foreach (var image in images) blocks.Add(ImagePart(image, protocol));
        return new JsonArray(new JsonObject { ["role"] = "user", [protocol == Protocol.Gemini ? "parts" : "content"] = blocks });
    }

    public static JsonObject BuildRequest(Protocol protocol, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools)
    {
        try { options.Validate(protocol, HasImages(protocol, history), tools.Count > 0); }
        catch (DomainValidationException error) { throw Invalid(error.Message); }
        var nativeTools = new JsonArray();
        foreach (var tool in tools)
            nativeTools.Add((JsonNode)(protocol switch
            {
                Protocol.Responses => new JsonObject { ["type"] = "function", ["name"] = tool.Name, ["description"] = tool.Description, ["parameters"] = Node(tool.Schema), ["strict"] = false },
                Protocol.Messages => new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["input_schema"] = Node(tool.Schema) },
                _ => new JsonObject { ["name"] = tool.Name, ["description"] = tool.Description, ["parametersJsonSchema"] = Node(tool.Schema) }
            }));
        JsonObject body;
        switch (protocol)
        {
            case Protocol.Responses:
                body = new() { ["model"] = options.Model.Id, ["input"] = history.DeepClone(), ["max_output_tokens"] = options.MaxOutputTokens, ["stream"] = options.Stream, ["store"] = false, ["include"] = new JsonArray("reasoning.encrypted_content"), ["tools"] = nativeTools };
                if (options.Thinking is ResponsesThinking responses) body["reasoning"] = new JsonObject { ["effort"] = responses.Effort };
                Extend(body, options.Extra, ["reasoning", "previous_response_id", "conversation"]);
                break;
            case Protocol.Messages:
                body = new() { ["model"] = options.Model.Id, ["messages"] = history.DeepClone(), ["max_tokens"] = options.MaxOutputTokens, ["stream"] = options.Stream, ["tools"] = nativeTools };
                if (options.Thinking is AdaptiveThinking adaptive)
                {
                    body["thinking"] = new JsonObject { ["type"] = "adaptive" };
                    body["output_config"] = new JsonObject { ["effort"] = adaptive.Effort };
                }
                else if (options.Thinking is BudgetThinking budget) body["thinking"] = new JsonObject { ["type"] = "enabled", ["budget_tokens"] = budget.Tokens };
                Extend(body, options.Extra, ["thinking", "output_config"]);
                break;
            case Protocol.Gemini:
                var generation = new JsonObject { ["maxOutputTokens"] = options.MaxOutputTokens, ["candidateCount"] = 1 };
                body = new() { ["contents"] = history.DeepClone(), ["generationConfig"] = generation };
                if (tools.Count > 0) body["tools"] = new JsonArray(new JsonObject { ["functionDeclarations"] = nativeTools });
                if (options.Thinking is GeminiLevelThinking level) generation["thinkingConfig"] = new JsonObject { ["thinkingLevel"] = level.Level };
                else if (options.Thinking is GeminiBudgetThinking geminiBudget) generation["thinkingConfig"] = new JsonObject { ["thinkingBudget"] = geminiBudget.Tokens };
                foreach (var (key, value) in options.Extra)
                {
                    if (key == "generationConfig")
                    {
                        if (value.ValueKind != JsonValueKind.Object) throw Invalid("generationConfig must be an object");
                        Extend(generation, value.EnumerateObject().ToDictionary(p => p.Name, p => p.Value), ["thinkingConfig", "maxOutputTokens", "candidateCount"]);
                    }
                    else Extend(body, new Dictionary<string, JsonElement> { [key] = value }, ["tools"]);
                }
                break;
            default: throw Invalid("unknown protocol");
        }
        return body;
    }

    public static Turn Parse(Protocol protocol, JsonElement raw)
    {
        try { return ParseCore(protocol, raw); }
        catch (OverflowException) { throw ProtocolError("token usage exceeds supported range"); }
    }

    private static Turn ParseCore(Protocol protocol, JsonElement raw)
    {
        var calls = new List<ToolCall>();
        var text = new System.Text.StringBuilder();
        JsonElement context, usage;
        string? stop;
        Usage normalized;
        if (Get(raw, "error").ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) || Str(raw, "type") == "error" || Str(raw, "status") == "failed")
            throw ProviderErrors.Classify((ushort)(Count(Get(raw, "error"), "code") ?? 400), raw);
        switch (protocol)
        {
            case Protocol.Responses:
                stop = Str(raw, "status");
                if (stop != "completed") throw Incomplete("Responses result did not complete");
                context = RequiredArray(raw, "output");
                foreach (var item in context.EnumerateArray())
                {
                    if (Str(item, "type") == "function_call") calls.Add(Call(RequiredString(item, "call_id"), RequiredString(item, "name"), Get(item, "arguments")));
                    else if (Str(item, "type") == "message")
                        foreach (var block in RequiredArray(item, "content").EnumerateArray())
                        {
                            if (Str(block, "type") == "refusal") throw new ProviderException(ErrorKind.Refusal, "provider declined the request");
                            if (Str(block, "type") == "output_text") text.Append(Str(block, "text"));
                        }
                }
                usage = Get(raw, "usage");
                var ri = Count(usage, "input_tokens");
                var rr = Count(Get(usage, "input_tokens_details"), "cached_tokens");
                normalized = new Usage { Input = ri, InputUncached = ri.HasValue && rr.HasValue && ri >= rr ? ri - rr : null, CacheRead = rr, Output = Count(usage, "output_tokens"), Reasoning = Count(Get(usage, "output_tokens_details"), "reasoning_tokens"), Total = Count(usage, "total_tokens"), Raw = Clone(usage) };
                break;
            case Protocol.Messages:
                var content = RequiredArray(raw, "content");
                stop = Str(raw, "stop_reason");
                if (stop is not ("end_turn" or "tool_use" or "stop_sequence")) throw Incomplete("Messages stopped without completing turn");
                foreach (var block in content.EnumerateArray())
                {
                    if (Str(block, "type") == "tool_use") calls.Add(Call(RequiredString(block, "id"), RequiredString(block, "name"), Get(block, "input")));
                    if (Str(block, "type") == "text") text.Append(Str(block, "text"));
                }
                context = Element(new JsonObject { ["role"] = "assistant", ["content"] = Node(content) });
                usage = Get(raw, "usage");
                var mi = Count(usage, "input_tokens");
                var mr = Count(usage, "cache_read_input_tokens");
                var mw = Count(usage, "cache_creation_input_tokens");
                var mo = Count(usage, "output_tokens");
                var input = mi.HasValue ? checked(mi + (mr ?? 0) + (mw ?? 0)) : null;
                normalized = new Usage { Input = input, InputUncached = mi, CacheRead = mr, CacheWrite = mw, Output = mo, Total = input.HasValue && mo.HasValue ? checked(input + mo) : null, Raw = Clone(usage) };
                break;
            case Protocol.Gemini:
                var candidates = RequiredArray(raw, "candidates");
                if (candidates.GetArrayLength() != 1) throw ProtocolError("expected exactly one Gemini candidate");
                var candidate = candidates[0];
                stop = Str(candidate, "finishReason");
                if (stop != "STOP") throw Incomplete("Gemini result blocked or incomplete");
                context = Get(candidate, "content");
                foreach (var part in RequiredArray(context, "parts").EnumerateArray())
                {
                    var function = Get(part, "functionCall");
                    if (function.ValueKind != JsonValueKind.Undefined)
                    {
                        var args = Get(function, "args");
                        calls.Add(Call(Str(function, "id"), RequiredString(function, "name"), args.ValueKind == JsonValueKind.Undefined ? Element(new JsonObject()) : args));
                    }
                    else if (Get(part, "thought").ValueKind != JsonValueKind.True) text.Append(Str(part, "text"));
                }
                usage = Get(raw, "usageMetadata");
                var gi = Count(usage, "promptTokenCount");
                var gr = Count(usage, "cachedContentTokenCount");
                var gc = Count(usage, "candidatesTokenCount");
                var gt = Count(usage, "thoughtsTokenCount");
                normalized = new Usage { Input = gi, InputUncached = gi.HasValue ? gi - Math.Min(gi.Value, gr ?? 0) : null, CacheRead = gr, Output = gc.HasValue || gt.HasValue ? checked((gc ?? 0) + (gt ?? 0)) : null, Reasoning = gt, Total = Count(usage, "totalTokenCount"), Raw = Clone(usage) };
                break;
            default: throw Invalid("unknown protocol");
        }
        return new Turn { Protocol = protocol, Text = text.ToString(), Calls = calls, Context = context.Clone(), Usage = normalized, StopReason = stop, Raw = raw.Clone() };
    }

    public static void ContinueWith(Protocol protocol, JsonArray history, Turn turn, IReadOnlyList<ToolResult> results)
    {
        if (turn.Protocol != protocol) throw Invalid("wrong turn protocol");
        if (results.Count != turn.Calls.Count) throw Invalid("return exactly one result for each tool call");
        var matched = new bool[turn.Calls.Count];
        foreach (var result in results)
        {
            var index = -1;
            for (var i = 0; i < turn.Calls.Count; i++)
            {
                var call = turn.Calls[i];
                if (!matched[i] && call.Id == result.Call.Id && call.Name == result.Call.Name && JsonElement.DeepEquals(call.Arguments, result.Call.Arguments)) { index = i; break; }
            }
            if (index < 0) throw Invalid("tool result does not match a pending call");
            matched[index] = true;
        }
        var appended = new JsonArray();
        if (protocol == Protocol.Responses)
        {
            if (turn.Context.ValueKind != JsonValueKind.Array) throw ProtocolError("invalid native context");
            foreach (var item in turn.Context.EnumerateArray()) appended.Add(Node(item));
        }
        else appended.Add(Node(turn.Context));
        var resultsContent = new JsonArray();
        foreach (var result in results)
        {
            if (protocol == Protocol.Gemini)
            {
                var response = new JsonObject { ["name"] = result.Call.Name, ["response"] = new JsonObject { [result.IsError ? "error" : "result"] = result.Text } };
                if (result.Call.Id is not null) response["id"] = result.Call.Id;
                if (result.Images.Count > 0) response["parts"] = new JsonArray(result.Images.Select(i => ImagePart(i, protocol)).ToArray());
                resultsContent.Add((JsonNode)new JsonObject { ["functionResponse"] = response });
            }
            else
            {
                var blocks = new JsonArray(new JsonObject { ["type"] = protocol == Protocol.Responses ? "input_text" : "text", ["text"] = protocol == Protocol.Responses && result.IsError ? $"Tool execution failed: {result.Text}" : result.Text });
                foreach (var image in result.Images) blocks.Add(ImagePart(image, protocol));
                if (protocol == Protocol.Responses) appended.Add((JsonNode)new JsonObject { ["type"] = "function_call_output", ["call_id"] = result.Call.Id, ["output"] = blocks });
                else resultsContent.Add((JsonNode)new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = result.Call.Id, ["content"] = blocks, ["is_error"] = result.IsError });
            }
        }
        if (protocol != Protocol.Responses) appended.Add((JsonNode)new JsonObject { ["role"] = "user", [protocol == Protocol.Gemini ? "parts" : "content"] = resultsContent });
        foreach (var item in appended) history.Add(item?.DeepClone());
    }

    public static ModelPage ParseModelPage(Protocol protocol, JsonElement raw)
    {
        var models = new List<Model>();
        foreach (var item in RequiredArray(raw, protocol == Protocol.Gemini ? "models" : "data").EnumerateArray())
        {
            var metadata = Get(item, "capabilities");
            var thinking = new List<Thinking>();
            var types = Get(Get(metadata, "thinking"), "types");
            var effort = Get(metadata, "effort");
            if (protocol == Protocol.Messages && Bool(Get(types, "adaptive"), "supported") == true && effort.ValueKind == JsonValueKind.Object)
                foreach (var entry in effort.EnumerateObject()) if (Bool(entry.Value, "supported") == true) thinking.Add(new AdaptiveThinking { Effort = entry.Name });
            var capabilities = protocol == Protocol.Messages ? new Capabilities { Vision = Bool(Get(metadata, "image_input"), "supported"), Tools = Bool(Get(metadata, "tool_use"), "supported"), MessagesBudget = Bool(Get(types, "enabled"), "supported"), Thinking = thinking, Source = metadata.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? "provider_metadata" : null } : Capabilities.Unknown;
            models.Add(new Model { Id = RequiredString(item, protocol == Protocol.Gemini ? "name" : "id"), Capabilities = capabilities, Raw = item.Clone() });
        }
        var next = protocol == Protocol.Gemini ? Str(raw, "nextPageToken") : Bool(raw, "has_more") == true ? RequiredString(raw, "last_id") : null;
        return new ModelPage { Models = models, Next = string.IsNullOrEmpty(next) ? null : next, Raw = raw.Clone() };
    }

    /// <summary>Legacy estimate using configured per-million rates, not an invoice.</summary>
    public static decimal? EstimateCost(Usage usage, decimal? inputPerMillion, decimal? outputPerMillion)
    {
        if (inputPerMillion < 0 || outputPerMillion < 0) throw Invalid("prices must be nonnegative");
        return usage.Input is { } input && usage.Output is { } output && inputPerMillion is { } p && outputPerMillion is { } q ? (input * p + output * q) / 1_000_000m : null;
    }

    private static bool HasImages(Protocol protocol, JsonArray history)
    {
        foreach (var message in history.OfType<JsonObject>())
        {
            var blocks = message[protocol == Protocol.Gemini ? "parts" : protocol == Protocol.Responses && String(message["type"]) == "function_call_output" ? "output" : "content"] as JsonArray;
            if (blocks is null) continue;
            foreach (var block in blocks.OfType<JsonObject>())
            {
                if (protocol == Protocol.Responses && String(block["type"]) == "input_image") return true;
                if (protocol == Protocol.Messages && (String(block["type"]) == "image" || String(block["type"]) == "tool_result" && block["content"] is JsonArray nested && nested.OfType<JsonObject>().Any(b => String(b["type"]) == "image"))) return true;
                if (protocol == Protocol.Gemini && (IsGeminiImage(block) || block["functionResponse"]?["parts"] is JsonArray parts && parts.OfType<JsonObject>().Any(IsGeminiImage))) return true;
            }
        }
        return false;
    }
    private static bool IsGeminiImage(JsonObject part) => new[] { "inlineData", "fileData" }.Any(key => part[key] is JsonObject data && String(data["mimeType"])?.StartsWith("image/", StringComparison.Ordinal) == true);
    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static JsonNode ImagePart(Image image, Protocol protocol) => protocol switch
    {
        Protocol.Responses => new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{image.Mime};base64,{image.Base64}" },
        Protocol.Messages => new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.Mime, ["data"] = image.Base64 } },
        _ => new JsonObject { ["inlineData"] = new JsonObject { ["mimeType"] = image.Mime, ["data"] = image.Base64 } }
    };
    private static ToolCall Call(string? id, string name, JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.String)
        {
            try { using var doc = JsonDocument.Parse(arguments.GetString()!); arguments = doc.RootElement.Clone(); }
            catch (JsonException) { throw ProtocolError("invalid tool argument JSON"); }
        }
        if (arguments.ValueKind != JsonValueKind.Object) throw ProtocolError("tool arguments must be a JSON object");
        return new ToolCall { Id = id, Name = name, Arguments = arguments.Clone() };
    }
    private static void Extend(JsonObject body, IReadOnlyDictionary<string, JsonElement> extra, string[] reserved)
    {
        foreach (var (key, value) in extra)
        {
            if (reserved.Contains(key, StringComparer.Ordinal) || body.ContainsKey(key)) throw Invalid($"native parameter collision: {key}");
            body[key] = Node(value);
        }
    }
    internal static JsonNode? Node(JsonElement element) => element.ValueKind == JsonValueKind.Undefined ? null : JsonNode.Parse(element.GetRawText());
    internal static JsonElement Element(JsonNode node) { using var document = JsonDocument.Parse(node.ToJsonString()); return document.RootElement.Clone(); }
    internal static JsonElement Get(JsonElement element, string key) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) ? value : default;
    internal static string? Str(JsonElement element, string key) => Get(element, key).ValueKind == JsonValueKind.String ? Get(element, key).GetString() : null;
    internal static ulong? Count(JsonElement element, string key) => Get(element, key).ValueKind == JsonValueKind.Number && Get(element, key).TryGetUInt64(out var count) ? count : null;
    private static bool? Bool(JsonElement element, string key) => Get(element, key).ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null };
    private static JsonElement Clone(JsonElement element) => element.ValueKind == JsonValueKind.Undefined ? DomainJson.ToElement((string?)null) : element.Clone();
    private static string RequiredString(JsonElement element, string key) => Str(element, key) ?? throw ProtocolError($"missing {key}");
    private static JsonElement RequiredArray(JsonElement element, string key) => Get(element, key).ValueKind == JsonValueKind.Array ? Get(element, key) : throw ProtocolError($"missing {key}");
    internal static ProviderException Invalid(string message) => new(ErrorKind.InvalidRequest, message);
    internal static ProviderException ProtocolError(string message) => new(ErrorKind.Protocol, message);
    internal static ProviderException Incomplete(string message) => new(ErrorKind.Incomplete, message);
}
