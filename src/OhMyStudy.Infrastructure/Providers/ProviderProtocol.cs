using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Providers;

/// <summary>Native protocol framing. Opaque context is copied without interpreting signatures.</summary>
public static class ProviderProtocol
{
    public static JsonArray Initial(Protocol protocol, string text, IReadOnlyList<Image> images)
    {
        if (!Enum.IsDefined(protocol)) throw Invalid("unknown protocol");
        var blocks = new JsonArray();
        blocks.Add((JsonNode)(protocol switch
        {
            Protocol.Responses => new JsonObject { ["type"] = "input_text", ["text"] = text },
            Protocol.Messages => new JsonObject { ["type"] = "text", ["text"] = text },
            _ => throw Invalid("unknown protocol")
        }));
        foreach (var image in images) blocks.Add(ImagePart(image, protocol));
        return new JsonArray(new JsonObject { ["role"] = "user", ["content"] = blocks });
    }

    /// <summary>Native request body. Responses and Messages are shaped by their official SDKs.</summary>
    public static JsonObject BuildRequest(Protocol protocol, RequestOptions options, JsonArray history, IReadOnlyList<Tool> tools)
    {
        if (protocol == Protocol.Responses) return ResponsesSdkClient.BuildBody(options, history, tools);
        if (protocol == Protocol.Messages) return MessagesSdkClient.BuildBody(options, history, tools);
        throw Invalid("unknown protocol");
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
                var rw = OptionalCacheCount(Get(usage, "input_tokens_details"), "cache_write_tokens");
                normalized = new Usage { Input = ri, InputUncached = ri.HasValue && rr.HasValue && rw.HasValue && rr <= ri && rw <= ri - rr ? ri - rr - rw : null, CacheRead = rr, CacheWrite = rw, Output = Count(usage, "output_tokens"), Reasoning = Count(Get(usage, "output_tokens_details"), "reasoning_tokens"), Total = Count(usage, "total_tokens"), Raw = Clone(usage) };
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
                var input = mi.HasValue && mr.HasValue && mw.HasValue ? checked(mi + mr + mw) : null;
                normalized = new Usage { Input = input, InputUncached = mi, CacheRead = mr, CacheWrite = mw, Output = mo, Total = input.HasValue && mo.HasValue ? checked(input + mo) : null, Raw = Clone(usage) };
                break;
            default: throw Invalid("unknown protocol");
        }
        return new Turn { Protocol = protocol, Text = text.ToString(), Calls = calls, Context = context.Clone(), Usage = normalized, StopReason = stop, Raw = raw.Clone() };
    }

    public static void ContinueWith(Protocol protocol, JsonArray history, Turn turn, IReadOnlyList<ToolResult> results)
    {
        if (!Enum.IsDefined(protocol)) throw Invalid("unknown protocol");
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
            var blocks = new JsonArray(new JsonObject { ["type"] = protocol == Protocol.Responses ? "input_text" : "text", ["text"] = protocol == Protocol.Responses && result.IsError ? $"Tool execution failed: {result.Text}" : result.Text });
            foreach (var image in result.Images) blocks.Add(ImagePart(image, protocol));
            if (protocol == Protocol.Responses) appended.Add((JsonNode)new JsonObject { ["type"] = "function_call_output", ["call_id"] = result.Call.Id, ["output"] = blocks });
            else resultsContent.Add((JsonNode)new JsonObject { ["type"] = "tool_result", ["tool_use_id"] = result.Call.Id, ["content"] = blocks, ["is_error"] = result.IsError });
        }
        if (protocol != Protocol.Responses) appended.Add((JsonNode)new JsonObject { ["role"] = "user", ["content"] = resultsContent });
        foreach (var item in appended) history.Add(item?.DeepClone());
    }

    public static ModelPage ParseModelPage(Protocol protocol, JsonElement raw)
    {
        if (!Enum.IsDefined(protocol)) throw Invalid("unknown protocol");
        var models = new List<Model>();
        foreach (var item in RequiredArray(raw, "data").EnumerateArray())
        {
            var metadata = Get(item, "capabilities");
            var thinking = new List<Thinking>();
            var types = Get(Get(metadata, "thinking"), "types");
            var effort = Get(metadata, "effort");
            if (protocol == Protocol.Messages && Bool(Get(types, "adaptive"), "supported") == true && effort.ValueKind == JsonValueKind.Object)
                foreach (var entry in effort.EnumerateObject()) if (Bool(entry.Value, "supported") == true) thinking.Add(new AdaptiveThinking { Effort = entry.Name });
            var capabilities = protocol == Protocol.Messages ? new Capabilities { Vision = Bool(Get(metadata, "image_input"), "supported"), Tools = Bool(Get(metadata, "tool_use"), "supported"), MessagesBudget = Bool(Get(types, "enabled"), "supported"), Thinking = thinking, Source = metadata.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) ? "provider_metadata" : null } : Capabilities.Unknown;
            models.Add(new Model { Id = RequiredString(item, "id"), Capabilities = capabilities, Raw = item.Clone() });
        }
        var next = Bool(raw, "has_more") == true ? RequiredString(raw, "last_id") : null;
        return new ModelPage { Models = models, Next = string.IsNullOrEmpty(next) ? null : next, Raw = raw.Clone() };
    }

    // Omitted optional cache fields on older protocol responses mean no cache use;
    // explicit null, malformed values, or missing usage remain unknown.
    private static ulong? OptionalCacheCount(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && !value.TryGetProperty(name, out _) ? 0 : Count(value, name);

    public static decimal? EstimateCost(Usage usage, ModelConfig model)
    {
        static decimal? Rate(double? value) => value is { } rate && double.IsFinite(rate) ? checked((decimal)rate) : null;
        try { return EstimateCost(usage, Rate(model.PriceInput), Rate(model.PriceOutput), Rate(model.PriceCacheRead), Rate(model.PriceCacheWrite)); }
        catch (OverflowException) { return null; }
    }

    public static decimal? EstimateCost(Usage usage, decimal? inputPerMillion, decimal? outputPerMillion,
        decimal? cacheReadPerMillion = null, decimal? cacheWritePerMillion = null)
    {
        if (inputPerMillion < 0 || outputPerMillion < 0 || cacheReadPerMillion < 0 || cacheWritePerMillion < 0)
            throw Invalid("prices must be nonnegative");
        if (usage.InputUncached is not { } input || usage.CacheRead is not { } read || usage.CacheWrite is not { } write
            || usage.Input is { } total && (decimal)input + read + write != total) return null;
        static bool Charge(ulong tokens, decimal? rate, ref decimal sum)
        {
            if (tokens == 0) return true;
            if (rate is null) return false;
            sum += rate.Value * tokens / 1_000_000m;
            return true;
        }
        try
        {
            decimal sum = 0;
            if (!Charge(input, inputPerMillion, ref sum) || !Charge(read, cacheReadPerMillion, ref sum) || !Charge(write, cacheWritePerMillion, ref sum)) return null;
            if (usage.Output is { } output && !Charge(output, outputPerMillion, ref sum)) return null;
            return sum;
        }
        catch (OverflowException) { return null; }
    }

    internal static bool HasImages(JsonArray history) => HasImages(Protocol.Responses, history);
    internal static bool HasImages(Protocol protocol, JsonArray history)
    {
        foreach (var message in history.OfType<JsonObject>())
        {
            var blocks = message[protocol == Protocol.Responses && String(message["type"]) == "function_call_output" ? "output" : "content"] as JsonArray;
            if (blocks is null) continue;
            foreach (var block in blocks.OfType<JsonObject>())
            {
                if (protocol == Protocol.Responses && String(block["type"]) == "input_image") return true;
                if (protocol == Protocol.Messages && (String(block["type"]) == "image" || String(block["type"]) == "tool_result" && block["content"] is JsonArray nested && nested.OfType<JsonObject>().Any(b => String(b["type"]) == "image"))) return true;
            }
        }
        return false;
    }
    private static string? String(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static JsonNode ImagePart(Image image, Protocol protocol) => protocol switch
    {
        Protocol.Responses => new JsonObject { ["type"] = "input_image", ["image_url"] = $"data:{image.Mime};base64,{image.Base64}" },
        Protocol.Messages => new JsonObject { ["type"] = "image", ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = image.Mime, ["data"] = image.Base64 } },
        _ => throw Invalid("unknown protocol")
    };
    private static ToolCall Call(string? id, string name, JsonElement arguments)
    {
        if (arguments.ValueKind == JsonValueKind.String && TryParse(arguments.GetString()!, out var parsed)) arguments = parsed;
        return new ToolCall { Id = id, Name = name, Arguments = Clone(arguments) };
    }

    private static bool TryParse(string json, out JsonElement parsed)
    {
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 256 });
            parsed = document.RootElement.Clone();
            return true;
        }
        catch (JsonException) { parsed = default; return false; }
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
    internal static ProviderException Interrupted(string message) => new(ErrorKind.Incomplete, message) { Diagnostic = "stream_interrupted" };
}
