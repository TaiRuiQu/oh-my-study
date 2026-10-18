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
    [JsonIgnore]
    public string Label => string.IsNullOrWhiteSpace(DisplayName) ? Id : DisplayName;
    public required Capabilities Capabilities { get; init; }
    public double? PriceInput { get; init; }
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
    public required bool ContainsHandwriting { get; init; }
    public required string Caption { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Grade
{
    public required Verdict Verdict { get; init; }
    public double? Earned { get; init; }
    public double? Possible { get; init; }
    public required GradeSource Source { get; init; }
    public required string Reason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record TeacherMark
{
    public required string Raw { get; init; }
    public required List<Region> Regions { get; init; }
    public Grade? Grade { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Attempt
{
    public required string StudentAnswer { get; init; }
    public required List<Region> StudentRegions { get; init; }
    public TeacherMark? Teacher { get; init; }
    public string? ReferenceKey { get; init; }
    public required string AiSolution { get; init; }
    public string? Conflict { get; init; }
    public required Grade Grade { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Question
{
    public required string DraftKey { get; init; }
    public required string Number { get; init; }
    public required string Subject { get; init; }
    public required string QuestionType { get; init; }
    public required List<Content> Content { get; init; }
    public required List<string> Options { get; init; }
    public double? Points { get; init; }
    public required string StandardAnswer { get; init; }
    public required string Explanation { get; init; }
    public required List<string> KnowledgePoints { get; init; }
    public required List<Region> Sources { get; init; }
    public required List<string> Uncertainties { get; init; }
    public Attempt? Attempt { get; init; }
    public required List<Question> Parts { get; init; }
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
    public required string Explanation { get; init; }
    public required List<Region> Sources { get; init; }
    public string? MatchedDraftKey { get; init; }
    public string? UncertainReason { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record Coverage
{
    public required string PageId { get; init; }
    public required string Status { get; init; }
    public required List<string> QuestionKeys { get; init; }
    public required string Reason { get; init; }
}

public sealed record GradingResult
{
    public required List<SavedQuestion> Questions { get; init; }
    public required List<Coverage> Coverage { get; init; }
    public required List<ReferenceAnswer> References { get; init; }
    public required List<JsonElement> ModelCalls { get; init; }
    public static GradingResult Empty => new() { Questions = [], Coverage = [], References = [], ModelCalls = [] };
}
