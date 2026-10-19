using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using System.Globalization;

namespace OhMyStudy.Domain;

/// <summary>Closed, source-generated JSON contract. Unknown CLR types fail instead of falling back to reflection.</summary>
public static class DomainJson
{
    public static DomainJsonContext Context { get; } = new(new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        RespectNullableAnnotations = true,
        AllowOutOfOrderMetadataProperties = true,
        MaxDepth = 256,
        Converters = { new SerdeDoubleConverter() }
    });

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, TypeInfo<T>());
    public static T Deserialize<T>(string json)
    {
        var info = TypeInfo<T>();
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = Context.Options.MaxDepth });
        ValidateReadShape(document.RootElement, info);
        return JsonSerializer.Deserialize(json, info) ?? throw new JsonException("Expected a non-null value");
    }
    public static JsonElement ToElement<T>(T value) => JsonSerializer.SerializeToElement(value, TypeInfo<T>());
    private static JsonTypeInfo<T> TypeInfo<T>() => Context.GetTypeInfo(typeof(T)) as JsonTypeInfo<T> ?? throw new NotSupportedException($"JSON contract is not registered: {typeof(T)}");

    // Generated metadata also closes two gaps between Serde and System.Text.Json:
    // duplicate known fields and null elements of Vec<T> must be rejected.
    private static void ValidateReadShape(JsonElement value, JsonTypeInfo info)
    {
        if (value.ValueKind == JsonValueKind.Null || info.Type == typeof(JsonElement)) return;
        if (info.Kind == JsonTypeInfoKind.Enumerable && value.ValueKind == JsonValueKind.Array && info.ElementType is { } elementType)
        {
            var elementInfo = Context.GetTypeInfo(elementType)!;
            foreach (var item in value.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.Null && elementType != typeof(JsonElement)) throw new JsonException("Collection elements cannot be null");
                ValidateReadShape(item, elementInfo);
            }
            return;
        }
        if (info.Kind != JsonTypeInfoKind.Object || value.ValueKind != JsonValueKind.Object) return;
        if (info.PolymorphismOptions is { } polymorphism)
        {
            if (value.EnumerateObject().Count(p => p.NameEquals(polymorphism.TypeDiscriminatorPropertyName)) > 1) throw new JsonException("Duplicate discriminator");
            if (value.TryGetProperty(polymorphism.TypeDiscriminatorPropertyName, out var kind) && kind.ValueKind == JsonValueKind.String)
                foreach (var derived in polymorphism.DerivedTypes)
                    if (derived.TypeDiscriminator is string discriminator && discriminator == kind.GetString()) { info = Context.GetTypeInfo(derived.DerivedType)!; break; }
        }
        HashSet<string> seen = new(StringComparer.Ordinal);
        foreach (var field in value.EnumerateObject())
        {
            if (info.Type == typeof(Provider) && field.NameEquals("has_key")) continue;
            var property = info.Properties.FirstOrDefault(p => p.Name == field.Name);
            if (property is null) continue;
            if (!seen.Add(field.Name)) throw new JsonException($"Duplicate field: {field.Name}");
            if (Context.GetTypeInfo(property.PropertyType) is { } propertyInfo) ValidateReadShape(field.Value, propertyInfo);
        }
    }
}

/// <summary>Serde enums are case-sensitive strings and never accept integer ordinals.</summary>
public sealed class StrictEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> Values = Enum.GetValues<T>().ToDictionary(v => Name(v), StringComparer.Ordinal);
    private static string Name(T value)
    {
        string name = JsonNamingPolicy.SnakeCaseLower.ConvertName(value.ToString());
        return typeof(T) == typeof(JobState) ? name.ToUpperInvariant() : name;
    }
    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Values.TryGetValue(reader.GetString()!, out var result) ? result : throw new JsonException($"Invalid {typeof(T).Name}");
    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options)
    {
        string name = Name(value);
        if (!Values.ContainsKey(name)) throw new JsonException($"Invalid {typeof(T).Name}");
        writer.WriteStringValue(name);
    }
}

public sealed class IgnoredInputBooleanConverter : JsonConverter<bool>
{
    public override bool Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) { reader.Skip(); return false; }
    public override void Write(Utf8JsonWriter writer, bool value, JsonSerializerOptions options) => writer.WriteBooleanValue(value);
}

public sealed class SerdeDoubleConverter : JsonConverter<double>
{
    public override double Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number && reader.TryGetDouble(out double value) && double.IsFinite(value) ? value : throw new JsonException("Expected a finite number");
    public override void Write(Utf8JsonWriter writer, double value, JsonSerializerOptions options)
    {
        if (!double.IsFinite(value)) writer.WriteNullValue();
        else writer.WriteRawValue(SerdeJson.Float(value), skipInputValidation: true);
    }
}

internal static class SerdeJson
{
    public static int ByteSize(JsonElement value, JsonTypeInfo info)
    {
        switch (value.ValueKind)
        {
            case JsonValueKind.Null: return 4;
            case JsonValueKind.True: return 4;
            case JsonValueKind.False: return 5;
            case JsonValueKind.String: return Encoding.UTF8.GetByteCount(Quote(value.GetString()!));
            case JsonValueKind.Number:
                return info.Type == typeof(double) || info.Type == typeof(double?) ? Float(value.GetDouble()).Length : value.GetRawText().Length;
            case JsonValueKind.Array:
                var items = value.EnumerateArray();
                var elementInfo = info.ElementType is { } elementType ? DomainJson.Context.GetTypeInfo(elementType)! : DomainJson.Context.JsonElement;
                return 2 + Math.Max(0, items.Count() - 1) + items.Sum(item => ByteSize(item, elementInfo));
            case JsonValueKind.Object:
                if (info.PolymorphismOptions is { } polymorphism && value.TryGetProperty(polymorphism.TypeDiscriminatorPropertyName, out var discriminator))
                    foreach (var derived in polymorphism.DerivedTypes)
                        if (derived.TypeDiscriminator is string kind && kind == discriminator.GetString()) { info = DomainJson.Context.GetTypeInfo(derived.DerivedType)!; break; }
                int size = 2, count = 0;
                foreach (var property in value.EnumerateObject())
                {
                    var propertyInfo = info.Properties.FirstOrDefault(p => p.Name == property.Name);
                    size += (count++ == 0 ? 0 : 1) + Encoding.UTF8.GetByteCount(Quote(property.Name)) + 1
                        + ByteSize(property.Value, propertyInfo is null ? DomainJson.Context.JsonElement : DomainJson.Context.GetTypeInfo(propertyInfo.PropertyType)!);
                }
                return size;
            default: throw new JsonException("Invalid JSON value");
        }
    }

    // Ryu's pretty format keeps decimal exponents -5..15 in fixed notation and preserves .0.
    public static string Float(double value)
    {
        string raw = value.ToString("R", CultureInfo.InvariantCulture);
        bool negative = raw.StartsWith('-');
        if (negative) raw = raw[1..];
        if (value == 0) return negative ? "-0.0" : "0.0";
        int e = raw.IndexOf('E');
        int exponent = e < 0 ? 0 : int.Parse(raw[(e + 1)..], CultureInfo.InvariantCulture);
        string mantissa = e < 0 ? raw : raw[..e];
        int point = mantissa.IndexOf('.');
        int beforePoint = point < 0 ? mantissa.Length : point;
        string digits = mantissa.Replace(".", "");
        int leading = digits.TakeWhile(c => c == '0').Count();
        int scientificExponent = beforePoint + exponent - leading - 1;
        digits = digits[leading..].TrimEnd('0');
        string formatted;
        if (scientificExponent is >= -5 and <= 15)
        {
            int place = scientificExponent + 1;
            formatted = place <= 0 ? "0." + new string('0', -place) + digits
                : place >= digits.Length ? digits + new string('0', place - digits.Length) + ".0"
                : digits.Insert(place, ".");
        }
        else formatted = digits[0] + (digits.Length > 1 ? "." + digits[1..] : "") + "e" + (scientificExponent >= 0 ? "+" : "") + scientificExponent.ToString(CultureInfo.InvariantCulture);
        return negative ? "-" + formatted : formatted;
    }

    public static string Quote(string value)
    {
        var result = new StringBuilder(value.Length + 2).Append('"');
        foreach (char c in value)
            result.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\b' => "\\b",
                '\f' => "\\f",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                < ' ' => $"\\u{(int)c:x4}",
                _ => c.ToString()
            });
        return result.Append('"').ToString();
    }
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, GenerationMode = JsonSourceGenerationMode.Metadata, RespectNullableAnnotations = true)]
[JsonSerializable(typeof(string))]
[JsonSerializable(typeof(JsonElement))]
[JsonSerializable(typeof(List<JsonElement>))]
[JsonSerializable(typeof(Dictionary<string, JsonElement>))]
[JsonSerializable(typeof(Provider))]
[JsonSerializable(typeof(List<Provider>))]
[JsonSerializable(typeof(Provider[]))]
[JsonSerializable(typeof(ModelConfig))]
[JsonSerializable(typeof(List<ModelConfig>))]
[JsonSerializable(typeof(ModelConfig[]))]
[JsonSerializable(typeof(Document))]
[JsonSerializable(typeof(List<Document>))]
[JsonSerializable(typeof(Document[]))]
[JsonSerializable(typeof(SourcePage))]
[JsonSerializable(typeof(List<SourcePage>))]
[JsonSerializable(typeof(SourcePage[]))]
[JsonSerializable(typeof(Asset))]
[JsonSerializable(typeof(List<Asset>))]
[JsonSerializable(typeof(Asset[]))]
[JsonSerializable(typeof(PageEdit))]
[JsonSerializable(typeof(List<PageEdit>))]
[JsonSerializable(typeof(PageEdit[]))]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(GradingInput))]
[JsonSerializable(typeof(Region))]
[JsonSerializable(typeof(List<Region>))]
[JsonSerializable(typeof(Region[]))]
[JsonSerializable(typeof(Content))]
[JsonSerializable(typeof(List<Content>))]
[JsonSerializable(typeof(Content[]))]
[JsonSerializable(typeof(Grade))]
[JsonSerializable(typeof(List<Grade>))]
[JsonSerializable(typeof(Grade[]))]
[JsonSerializable(typeof(TeacherMark))]
[JsonSerializable(typeof(Attempt))]
[JsonSerializable(typeof(Question))]
[JsonSerializable(typeof(List<Question>))]
[JsonSerializable(typeof(Question[]))]
[JsonSerializable(typeof(SavedQuestion))]
[JsonSerializable(typeof(List<SavedQuestion>))]
[JsonSerializable(typeof(SavedQuestion[]))]
[JsonSerializable(typeof(ReferenceAnswer))]
[JsonSerializable(typeof(List<ReferenceAnswer>))]
[JsonSerializable(typeof(ReferenceAnswer[]))]
[JsonSerializable(typeof(Coverage))]
[JsonSerializable(typeof(List<Coverage>))]
[JsonSerializable(typeof(Coverage[]))]
[JsonSerializable(typeof(GradingResult))]
[JsonSerializable(typeof(Protocol))]
[JsonSerializable(typeof(ImportMode))]
[JsonSerializable(typeof(Verdict))]
[JsonSerializable(typeof(GradeSource))]
[JsonSerializable(typeof(Capabilities))]
[JsonSerializable(typeof(Thinking))]
[JsonSerializable(typeof(List<Thinking>))]
[JsonSerializable(typeof(Thinking[]))]
[JsonSerializable(typeof(Model))]
[JsonSerializable(typeof(List<Model>))]
[JsonSerializable(typeof(Model[]))]
[JsonSerializable(typeof(Image))]
[JsonSerializable(typeof(List<Image>))]
[JsonSerializable(typeof(Image[]))]
[JsonSerializable(typeof(Tool))]
[JsonSerializable(typeof(List<Tool>))]
[JsonSerializable(typeof(Tool[]))]
[JsonSerializable(typeof(ToolCall))]
[JsonSerializable(typeof(List<ToolCall>))]
[JsonSerializable(typeof(ToolCall[]))]
[JsonSerializable(typeof(ToolResult))]
[JsonSerializable(typeof(List<ToolResult>))]
[JsonSerializable(typeof(ToolResult[]))]
[JsonSerializable(typeof(RequestOptions))]
[JsonSerializable(typeof(Usage))]
[JsonSerializable(typeof(Turn))]
[JsonSerializable(typeof(ErrorKind))]
[JsonSerializable(typeof(ProviderError))]
[JsonSerializable(typeof(ModelPage))]
[JsonSerializable(typeof(ExecutionMode))]
[JsonSerializable(typeof(CapabilityState))]
[JsonSerializable(typeof(ExecutorKind))]
[JsonSerializable(typeof(ToolKind))]
[JsonSerializable(typeof(JobState))]
[JsonSerializable(typeof(DiagnosticKind))]
[JsonSerializable(typeof(PermissionChoice))]
[JsonSerializable(typeof(Capability))]
[JsonSerializable(typeof(ExecutionPolicy))]
[JsonSerializable(typeof(ExecutionCall))]
[JsonSerializable(typeof(PermissionReview))]
[JsonSerializable(typeof(Job))]
[JsonSerializable(typeof(List<Job>))]
[JsonSerializable(typeof(Job[]))]
[JsonSerializable(typeof(JobEvent))]
[JsonSerializable(typeof(JobActivity))]
[JsonSerializable(typeof(List<JobEvent>))]
[JsonSerializable(typeof(JobEvent[]))]
[JsonSerializable(typeof(List<AppSettings>))]
[JsonSerializable(typeof(AppSettings[]))]
[JsonSerializable(typeof(List<GradingInput>))]
[JsonSerializable(typeof(GradingInput[]))]
[JsonSerializable(typeof(List<TeacherMark>))]
[JsonSerializable(typeof(TeacherMark[]))]
[JsonSerializable(typeof(List<Attempt>))]
[JsonSerializable(typeof(Attempt[]))]
[JsonSerializable(typeof(List<GradingResult>))]
[JsonSerializable(typeof(GradingResult[]))]
[JsonSerializable(typeof(List<Capabilities>))]
[JsonSerializable(typeof(Capabilities[]))]
[JsonSerializable(typeof(List<RequestOptions>))]
[JsonSerializable(typeof(RequestOptions[]))]
[JsonSerializable(typeof(List<Usage>))]
[JsonSerializable(typeof(Usage[]))]
[JsonSerializable(typeof(List<Turn>))]
[JsonSerializable(typeof(Turn[]))]
[JsonSerializable(typeof(List<ProviderError>))]
[JsonSerializable(typeof(ProviderError[]))]
[JsonSerializable(typeof(List<ModelPage>))]
[JsonSerializable(typeof(ModelPage[]))]
[JsonSerializable(typeof(List<Capability>))]
[JsonSerializable(typeof(Capability[]))]
[JsonSerializable(typeof(List<ExecutionPolicy>))]
[JsonSerializable(typeof(ExecutionPolicy[]))]
[JsonSerializable(typeof(List<ExecutionCall>))]
[JsonSerializable(typeof(ExecutionCall[]))]
[JsonSerializable(typeof(List<PermissionReview>))]
[JsonSerializable(typeof(PermissionReview[]))]
[JsonSerializable(typeof(List<TextContent>))]
[JsonSerializable(typeof(TextContent[]))]
[JsonSerializable(typeof(List<FormulaContent>))]
[JsonSerializable(typeof(FormulaContent[]))]
[JsonSerializable(typeof(List<TableContent>))]
[JsonSerializable(typeof(TableContent[]))]
[JsonSerializable(typeof(List<ImageContent>))]
[JsonSerializable(typeof(ImageContent[]))]
[JsonSerializable(typeof(List<ResponsesThinking>))]
[JsonSerializable(typeof(ResponsesThinking[]))]
[JsonSerializable(typeof(List<AdaptiveThinking>))]
[JsonSerializable(typeof(AdaptiveThinking[]))]
[JsonSerializable(typeof(List<BudgetThinking>))]
[JsonSerializable(typeof(BudgetThinking[]))]
[JsonSerializable(typeof(List<GeminiLevelThinking>))]
[JsonSerializable(typeof(GeminiLevelThinking[]))]
[JsonSerializable(typeof(List<GeminiBudgetThinking>))]
[JsonSerializable(typeof(GeminiBudgetThinking[]))]
public partial class DomainJsonContext : JsonSerializerContext;
