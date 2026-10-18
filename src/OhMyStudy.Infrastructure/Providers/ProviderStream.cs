using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Domain;
using static OhMyStudy.Infrastructure.Providers.ProviderProtocol;

namespace OhMyStudy.Infrastructure.Providers;

public sealed record ProviderProgress(string? Text = null, string? ToolName = null);
public sealed record SseEvent(string? Event, string Data);

/// <summary>Incremental SSE framing with strict UTF-8, byte limits and EOF validation.</summary>
public sealed class SseDecoder
{
    public const int MaximumBytes = 32 * 1024 * 1024;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);
    private readonly List<byte> _line = [];
    private readonly List<string> _data = [];
    private string? _event;
    private int _eventBytes;
    private bool _pendingCr;
    private bool _firstLine = true;

    public IReadOnlyList<SseEvent> Push(ReadOnlySpan<byte> bytes)
    {
        var result = new List<SseEvent>();
        foreach (var b in bytes)
        {
            if (_pendingCr) { _pendingCr = false; if (b == (byte)'\n') continue; }
            if (b is (byte)'\r' or (byte)'\n')
            {
                ProcessLine(result);
                _pendingCr = b == (byte)'\r';
            }
            else
            {
                _line.Add(b);
                if (_line.Count + _eventBytes > MaximumBytes) throw ProtocolError("SSE event exceeds limit");
            }
        }
        return result;
    }
    private void ProcessLine(List<SseEvent> result)
    {
        string line;
        try { line = StrictUtf8.GetString(System.Runtime.InteropServices.CollectionsMarshal.AsSpan(_line)); }
        catch (DecoderFallbackException) { throw ProtocolError("invalid UTF-8 SSE"); }
        _line.Clear();
        if (_firstLine) { _firstLine = false; line = line.TrimStart('\uFEFF'); }
        if (line.Length == 0)
        {
            if (_data.Count > 0) result.Add(new SseEvent(_event, string.Join('\n', _data)));
            _data.Clear(); _event = null; _eventBytes = 0;
        }
        else if (!line.StartsWith(':'))
        {
            var colon = line.IndexOf(':');
            var field = colon < 0 ? line : line[..colon];
            var value = colon < 0 ? "" : line[(colon + 1)..];
            if (value.StartsWith(' ')) value = value[1..];
            if (field == "data") { _data.Add(value); _eventBytes += StrictUtf8.GetByteCount(value) + 1; }
            else if (field == "event") _event = value;
            if (_eventBytes > MaximumBytes) throw ProtocolError("SSE event exceeds limit");
        }
    }
    public void Finish()
    {
        if (_line.Count > 0 || _data.Count > 0) throw Incomplete("truncated SSE frame");
    }
}

/// <summary>Native state reducer. A successful EOF requires a complete native result.</summary>
public sealed class ProviderStreamAssembler(Protocol protocol)
{
    private readonly SseDecoder _decoder = new();
    private readonly SortedDictionary<int, JsonObject> _blocks = [];
    private readonly Dictionary<int, StringBuilder> _arguments = [];
    private readonly HashSet<int> _active = [];
    private readonly JsonArray _events = [];
    private JsonObject _raw = [];
    private int _bytes;
    private bool _started, _terminal;

    public IReadOnlyList<ProviderProgress> Push(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > SseDecoder.MaximumBytes - _bytes) throw ProtocolError("stream exceeds limit");
        _bytes += bytes.Length;
        var progress = new List<ProviderProgress>();
        foreach (var frame in _decoder.Push(bytes))
        {
            if (frame.Data == "[DONE]") continue;
            JsonObject value;
            try { value = JsonNode.Parse(frame.Data) as JsonObject ?? throw ProtocolError("invalid SSE JSON"); }
            catch (JsonException) { throw ProtocolError("invalid SSE JSON"); }
            var kind = Text(value, "type") ?? frame.Event ?? "";
            if (kind is "error" or "response.failed") throw ProviderErrors.Classify(400, Element(value));
            if (_terminal) throw ProtocolError("event received after terminal response");
            switch (protocol)
            {
                case Protocol.Responses: Responses(value, kind, progress); break;
                case Protocol.Messages: Messages(value, kind, progress); break;
                case Protocol.Gemini: Gemini(value, progress); break;
                default: throw Invalid("unknown protocol");
            }
            _events.Add(value.DeepClone());
        }
        return progress;
    }

    private void Responses(JsonObject value, string kind, List<ProviderProgress> progress)
    {
        switch (kind)
        {
            case "response.refusal.delta": case "response.refusal.done": throw new ProviderException(ErrorKind.Refusal, "provider declined the request");
            case "response.output_text.delta": if (Text(value, "delta") is { } text) progress.Add(new(Text: text)); break;
            case "response.output_item.added":
                if (value["item"] is JsonObject item && Text(item, "type") == "function_call") progress.Add(new(ToolName: Text(item, "name") ?? ""));
                break;
            case "response.completed": _raw = value["response"]?.DeepClone() as JsonObject ?? throw ProtocolError("missing response"); _terminal = true; break;
            case "response.incomplete": throw Incomplete("Responses stream incomplete");
        }
    }

    private void Messages(JsonObject value, string kind, List<ProviderProgress> progress)
    {
        switch (kind)
        {
            case "message_start":
                if (_started) throw ProtocolError("duplicate message_start");
                _started = true;
                _raw = value["message"]?.DeepClone() as JsonObject ?? throw ProtocolError("missing message");
                break;
            case "content_block_start":
                if (!_started) throw ProtocolError("content before message_start");
                var index = Index(value);
                var block = value["content_block"]?.DeepClone() as JsonObject ?? throw ProtocolError("missing content block");
                if (!_blocks.TryAdd(index, block)) throw ProtocolError("duplicate content block index");
                _active.Add(index);
                if (Text(block, "type") == "tool_use") progress.Add(new(ToolName: Text(block, "name") ?? ""));
                break;
            case "content_block_delta":
                index = Index(value);
                if (!_active.Contains(index)) throw ProtocolError("delta outside active content block");
                block = _blocks[index];
                var delta = value["delta"] as JsonObject ?? throw ProtocolError("missing delta");
                switch (Text(delta, "type"))
                {
                    case "text_delta": Append(block, "text", delta); progress.Add(new(Text: Text(delta, "text"))); break;
                    case "thinking_delta": Append(block, "thinking", delta); break;
                    case "signature_delta": Append(block, "signature", delta); break;
                    case "input_json_delta":
                        if (!_arguments.TryGetValue(index, out var builder)) _arguments[index] = builder = new StringBuilder();
                        builder.Append(Text(delta, "partial_json") ?? throw ProtocolError("missing partial_json"));
                        break;
                }
                break;
            case "content_block_stop":
                index = Index(value);
                if (!_active.Remove(index)) throw ProtocolError("stop outside active block");
                if (_arguments.Remove(index, out var arguments) && arguments.Length > 0)
                {
                    try { _blocks[index]["input"] = JsonNode.Parse(arguments.ToString()); }
                    catch (JsonException) { throw ProtocolError("truncated tool arguments"); }
                }
                break;
            case "message_delta":
                if (!_started) throw ProtocolError("delta before message_start");
                if (value["delta"] is JsonObject deltaObject) foreach (var (key, part) in deltaObject) _raw[key] = part?.DeepClone();
                if (value["usage"] is JsonObject usage)
                {
                    if (_raw["usage"] is not JsonObject) _raw["usage"] = new JsonObject();
                    foreach (var (key, part) in usage) _raw["usage"]![key] = part?.DeepClone();
                }
                break;
            case "message_stop":
                if (!_started || _active.Count != 0 || _arguments.Count != 0) throw ProtocolError("unclosed or missing message/content block");
                _raw["content"] = new JsonArray(_blocks.Values.Select(b => (JsonNode)b.DeepClone()).ToArray());
                _terminal = true;
                break;
        }
    }

    private void Gemini(JsonObject value, List<ProviderProgress> progress)
    {
        if (value["error"] is not null) throw ProviderErrors.Classify((ushort)(Count(Element(value["error"]!), "code") ?? 400), Element(value));
        if (value["candidates"] is JsonArray candidates)
        {
            if (candidates.Count != 1 || candidates[0] is not JsonObject candidate) throw ProtocolError("expected one streaming Gemini candidate");
            if (_raw["candidates"] is not JsonArray) _raw["candidates"] = new JsonArray(new JsonObject { ["content"] = new JsonObject { ["role"] = "model", ["parts"] = new JsonArray() } });
            var target = _raw["candidates"]![0]!;
            if (candidate["content"] is JsonObject content && content["parts"] is JsonArray parts)
                foreach (var part in parts.OfType<JsonObject>())
                {
                    if (part["thought"]?.ToString() != "true" && Text(part, "text") is { } text) progress.Add(new(Text: text));
                    if (part["functionCall"] is JsonObject call && Text(call, "name") is { } name) progress.Add(new(ToolName: name));
                    ((JsonArray)target["content"]!["parts"]!).Add(part.DeepClone());
                }
            if (candidate["finishReason"] is { } reason) target["finishReason"] = reason.DeepClone();
        }
        if (value["usageMetadata"] is { } usage) _raw["usageMetadata"] = usage.DeepClone();
    }
    public Turn Finish()
    {
        _decoder.Finish();
        if (protocol != Protocol.Gemini && !_terminal) throw Incomplete("stream ended before terminal event");
        _raw["_stream_events"] = _events.DeepClone();
        return Parse(protocol, Element(_raw));
    }
    private static string? Text(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static int Index(JsonObject node) => node["index"] is JsonValue value && value.TryGetValue<int>(out var index) && index >= 0 ? index : throw ProtocolError("missing block index");
    private static void Append(JsonObject block, string key, JsonObject delta) => block[key] = (Text(block, key) ?? "") + (Text(delta, key) ?? throw ProtocolError("invalid text delta"));
}
