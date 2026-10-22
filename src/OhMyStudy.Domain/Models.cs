using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyStudy.Domain;

[JsonConverter(typeof(StrictEnumConverter<ImportMode>))]
public enum ImportMode { TeacherGraded, AiGrading, Blank }
[JsonConverter(typeof(StrictEnumConverter<Verdict>))]
public enum Verdict { Correct, Partial, Incorrect, Unanswered, Uncertain }
[JsonConverter(typeof(StrictEnumConverter<GradeSource>))]
public enum GradeSource { Teacher, AiReference, AiSolution, Manual }

public sealed record Provider : IJsonOnDeserialized
{
    private bool _hasKey;
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required Protocol Protocol { get; init; }
    public required string BaseUrl { get; init; }
    public string? KeyRef { get; init; }
    // This is a credential-store observation; persisted or caller-supplied JSON cannot assert it.
    [JsonConverter(typeof(IgnoredInputBooleanConverter))]
    public bool HasKey { get => _hasKey; init => _hasKey = value; }
    public required List<ModelConfig> Models { get; init; }
    void IJsonOnDeserialized.OnDeserialized() => _hasKey = false;
}

public sealed record ModelConfig
{
    public required string Id { get; init; }
    public string? DisplayName { get; init; }
    public ulong? ContextWindow { get; init; }
    public ulong? MaxOutputTokens { get; init; }
    public List<string>? InputModalities { get; init; }
    public List<string>? OutputModalities { get; init; }
    public bool? Reasoning { get; init; }
    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
    public required Capabilities Capabilities { get; init; }
    public double? PriceInput { get; init; }
    public double? PriceCacheRead { get; init; }
    public double? PriceCacheWrite { get; init; }
    public double? PriceOutput { get; init; }
    public required string Currency { get; init; }
}

public sealed record Document
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required ImportMode Mode { get; init; }
    public required string CreatedAt { get; init; }
    public required List<SourcePage> Pages { get; init; }
    public required bool PendingPdfPages { get; init; }
}

public sealed record SourcePage
{
    public required string Id { get; init; }
    public required string AssetId { get; init; }
    public string? OriginalAssetId { get; init; }
    public uint? PdfPageIndex { get; init; }
    public required uint PageIndex { get; init; }
    public required ushort Rotation { get; init; }
    public uint? Width { get; init; }
    public uint? Height { get; init; }
    public required string PreviewStatus { get; init; }
    public required string Role { get; init; }
}

public sealed record Asset
{
    public required string Id { get; init; }
    public required string Mime { get; init; }
    public required ulong ByteSize { get; init; }
    public uint? Width { get; init; }
    public uint? Height { get; init; }
}

public sealed record PageEdit
{
    public required string Id { get; init; }
    public required ushort Rotation { get; init; }
}

public sealed record AppSettings
{
    public bool LegacyNoticeDismissed { get; init; }
    public string? DefaultModel { get; init; }
    public required string ExecutionMode { get; init; }
    public required bool SandboxNetworking { get; init; }
    public static AppSettings Default => new() { DefaultModel = null, ExecutionMode = "bypass_permission", SandboxNetworking = false };
}

public sealed record GradingInput
{
    public required Document Document { get; init; }
    public required Provider Provider { get; init; }
    public required ModelConfig Model { get; init; }
    public Thinking? Thinking { get; init; }
    public required bool AllowUnknown { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Region
{
    public required string PageId { get; init; }
    public required double X { get; init; }
    public required double Y { get; init; }
    public required double Width { get; init; }
    public required double Height { get; init; }
}

[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(TextContent), "text")]
[JsonDerivedType(typeof(FormulaContent), "formula")]
[JsonDerivedType(typeof(TableContent), "table")]
[JsonDerivedType(typeof(ImageContent), "image")]
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public abstract record Content;
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TextContent : Content { public required string Text { get; init; } }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record FormulaContent : Content { public required string Latex { get; init; } }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TableContent : Content { public required List<List<string>> Rows { get; init; } }
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ImageContent : Content
{
    public required Region Region { get; init; }
    public bool ContainsHandwriting { get; init; }
    public string Caption { get; set; } = "";
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Grade
{
    public required Verdict Verdict { get; init; }
    public double? Earned { get; init; }
    public double? Possible { get; init; }
    public required GradeSource Source { get; init; }
    public string Reason { get; set; } = "";
}

// Auxiliary defaults use setters: source-generated constructors otherwise assign missing init-only properties to null.
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TeacherMark
{
    public string Raw { get; set; } = "";
    public List<Region> Regions { get; set; } = [];
    public Grade? Grade { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Attempt
{
    public string StudentAnswer { get; set; } = "";
    public List<Region> StudentRegions { get; set; } = [];
    public TeacherMark? Teacher { get; init; }
    public string? ReferenceKey { get; init; }
    public string AiSolution { get; set; } = "";
    public string? Conflict { get; init; }
    public Grade Grade { get; set; } = new() { Verdict = Verdict.Uncertain, Source = GradeSource.AiSolution, Reason = "No grade provided." };
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Question
{
    public required string DraftKey { get; init; }
    public required string Number { get; init; }
    public string Subject { get; set; } = "";
    public string QuestionType { get; set; } = "";
    public required List<Content> Content { get; init; }
    public List<string> Options { get; set; } = [];
    public double? Points { get; init; }
    public string StandardAnswer { get; set; } = "";
    public string Explanation { get; set; } = "";
    public List<string> KnowledgePoints { get; set; } = [];
    public required List<Region> Sources { get; init; }
    public List<string> Uncertainties { get; set; } = [];
    public Attempt? Attempt { get; init; }
    public List<Question> Parts { get; set; } = [];
}

public sealed record SavedQuestion
{
    public required string Id { get; init; }
    public required string DocumentId { get; init; }
    public required string UpdatedAt { get; init; }
    public required Question Question { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ReferenceAnswer
{
    public required string Key { get; init; }
    public required string Number { get; init; }
    public required string Answer { get; init; }
    public string Explanation { get; set; } = "";
    public required List<Region> Sources { get; init; }
    public string? MatchedDraftKey { get; init; }
    public string? UncertainReason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Coverage
{
    public required string PageId { get; init; }
    public required string Status { get; init; }
    public List<string> QuestionKeys { get; set; } = [];
    public string Reason { get; set; } = "";
}

public sealed record GradingResult
{
    public required List<SavedQuestion> Questions { get; init; }
    public required List<Coverage> Coverage { get; init; }
    public required List<ReferenceAnswer> References { get; init; }
    public required List<JsonElement> ModelCalls { get; init; }
    public static GradingResult Empty => new() { Questions = [], Coverage = [], References = [], ModelCalls = [] };
}
