using System.Text.Json.Serialization;

namespace OhMyStudy.Domain;

public sealed record ExamWorkflowSettings
{
    public string? MinerUTokenRef { get; init; }
    public SearchModel Splitter { get; set; } = new("https://api.deepseek.com", "deepseek-flash");
    public string? SplitterModel { get; init; }
    public string? ClassifierModel { get; init; }
    public string? GraderModel { get; init; }
    public string? RegionModelPath { get; init; }
    public string? ChineseRegionModelPath { get; init; }
    public int ParallelQuestions { get; set; } = 3;
    public int MaxQuestionRounds { get; set; } = 4;
    public int SplitterMaxTokens { get; set; } = 32768;
}

public sealed record WorkflowModel(Provider Provider, ModelConfig Model);

[JsonConverter(typeof(StrictEnumConverter<ExamFormat>))]
public enum ExamFormat { BlankPaper, InlineAnswers, MarkedInlineAnswers, SeparateAnswerSheet, MarkedAnswerSheet, Uncertain }

[JsonConverter(typeof(StrictEnumConverter<ExamPageKind>))]
public enum ExamPageKind { QuestionPaper, AnswerSheet, Mixed, Reference, Uncertain }

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExamLayout
{
    public required ExamFormat Format { get; init; }
    public required double Confidence { get; init; }
    public required string Reason { get; init; }
    public required List<ExamPageLayout> Pages { get; init; }
    public required List<string> Uncertainties { get; init; }

    [JsonIgnore]
    public bool HasSeparateAnswers => Format is ExamFormat.SeparateAnswerSheet or ExamFormat.MarkedAnswerSheet || Pages.Any(p => p.Kind == ExamPageKind.AnswerSheet);

    public ImportMode ResolveMode(ImportMode requested) => requested != ImportMode.Auto || Confidence < .8 || Format == ExamFormat.Uncertain ? requested : Format switch
    {
        ExamFormat.BlankPaper => ImportMode.Blank,
        ExamFormat.SeparateAnswerSheet when !Pages.Any(p => p.HasStudentWork || p.HasTeacherMarks) => ImportMode.Blank,
        ExamFormat.MarkedInlineAnswers or ExamFormat.MarkedAnswerSheet => ImportMode.TeacherGraded,
        _ => ImportMode.AiGrading
    };

    public Document Apply(Document original)
    {
        DomainValidationException.Require(double.IsFinite(Confidence) && Confidence is >= 0 and <= 1, "Invalid exam format confidence.");
        DomainValidationException.Require(Pages.Count == original.Pages.Count && Pages.Select(p => p.PageId).Distinct().Count() == Pages.Count
            && Pages.Select(p => p.PageId).ToHashSet().SetEquals(original.Pages.Select(p => p.Id)), "Exam format classification must identify every source page exactly once.");
        foreach (var page in Pages)
        {
            var source = original.Pages.Single(p => p.Id == page.PageId);
            DomainValidationException.Require((source.Role == "reference") == (page.Kind == ExamPageKind.Reference), "Classification cannot change explicit reference-page roles.");
            DomainValidationException.Require(source.Role != "student" || page.Kind is ExamPageKind.AnswerSheet or ExamPageKind.Uncertain,
                "Classification must preserve explicitly supplied student-answer pages.");
        }
        DomainValidationException.Require(!HasSeparateAnswers || Pages.Any(p => p.Kind is ExamPageKind.AnswerSheet or ExamPageKind.Mixed), "Separate answers require an identified answer sheet.");
        DomainValidationException.Require(Format != ExamFormat.BlankPaper || !Pages.Any(p => p.HasStudentWork || p.HasTeacherMarks || p.Kind == ExamPageKind.AnswerSheet),
            "A blank exam classification conflicts with visible work, teacher marks, or an answer sheet.");
        return original with { Mode = ResolveMode(original.Mode), Pages = original.Pages.Select(page => page with
        { Role = Pages.Single(p => p.PageId == page.Id).Kind == ExamPageKind.AnswerSheet ? "student" : page.Role }).ToList() };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExamPageLayout
{
    public required string PageId { get; init; }
    public required ExamPageKind Kind { get; init; }
    public required bool HasStudentWork { get; init; }
    public required bool HasTeacherMarks { get; init; }
    public required string Note { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ItemAnswerBinding
{
    public required string ItemKey { get; init; }
    public required string Status { get; init; }
    public List<string> BlockIds { get; init; } = [];
    public required List<Region> Regions { get; init; }
    public required string Reason { get; init; }
}

public sealed record OcrBlock(string Id, string Kind, string Markdown, Region Region, string? AssetId = null);
public sealed record OcrPage(string PageId, string Markdown, List<OcrBlock> Blocks);
public sealed record DetectedQuestionRegion(string Id, Region Region, double Confidence, string Model);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuestionSplitPlan
{
    public string? Name { get; init; }
    public string? Subject { get; init; }
    public required List<SplitQuestion> Questions { get; init; }
    public required List<Coverage> Coverage { get; init; }
    public required List<ReferenceAnswer> References { get; init; }
    public required List<string> IgnoredBlockIds { get; init; }
    public required List<string> IgnoredDetectionIds { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SplitQuestion
{
    public required string Key { get; init; }
    public required string Number { get; init; }
    public required string Markdown { get; init; }
    public string Subject { get; set; } = "";
    public string QuestionType { get; set; } = "";
    public required List<string> BlockIds { get; init; }
    public required List<string> DetectionIds { get; init; }
    public required List<Region> StudentRegions { get; init; }
    public required bool HasStudentWork { get; init; }
    public required List<ItemAnswerBinding> AnswerBindings { get; init; }
    public required List<SplitQuestionItem> Items { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SplitQuestionItem
{
    public required string Key { get; init; }
    public string Label { get; set; } = "";
    public string Prompt { get; set; } = "";
    public List<string> Options { get; set; } = [];
    public double? Points { get; init; }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuestionSolution
{
    public required List<ItemSolution> Items { get; init; }
    public List<string> KnowledgePoints { get; set; } = [];
    public List<string> Uncertainties { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ItemSolution(string Key, string Answer, string Explanation);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record QuestionAssessment
{
    public required List<ItemAssessment> Items { get; init; }
    public List<string> Uncertainties { get; set; } = [];
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ItemAssessment(string Key, Attempt Attempt);
