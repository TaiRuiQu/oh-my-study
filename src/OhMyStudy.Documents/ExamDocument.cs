namespace OhMyStudy.Documents;

public enum ExamPaper { A4Portrait, A3Landscape }
public enum ExamEdition { Questions, Answers }
public enum ExamColumns { Default = 0, One = 1, Two = 2, Three = 3, Four = 4 }
public sealed record ExamSegment(string Text, bool IsFormula = false, IReadOnlyList<IReadOnlyList<string>>? Rows = null,
    ExamImage? Image = null, ExamMermaid? Mermaid = null);
public sealed record ExamImage(byte[] Bytes, string Caption = "", bool ContainsHandwriting = false, double WidthPercent = 100)
{
    private double widthPercent = ExamMediaWidth.Validate(WidthPercent);
    public double WidthPercent
    {
        get => widthPercent;
        init => widthPercent = ExamMediaWidth.Validate(value);
    }
}
public sealed record ExamMermaid(string Code, string Caption = "", double WidthPercent = 100)
{
    private double widthPercent = ExamMediaWidth.Validate(WidthPercent);
    public double WidthPercent
    {
        get => widthPercent;
        init => widthPercent = ExamMediaWidth.Validate(value);
    }
}
internal static class ExamMediaWidth
{
    internal static double Validate(double value)
    {
        if (!double.IsFinite(value) || value is < 1 or > 100) throw new ArgumentOutOfRangeException(nameof(value), "The width percentage must be finite and between 1 and 100.");
        return value;
    }
}
public sealed record ExamPart(string Label, IReadOnlyList<ExamSegment> Body, string? Answer = null, double BlankMillimetres = 12,
    string? Explanation = null, IReadOnlyList<string>? Options = null, IReadOnlyList<ExamPart>? Parts = null,
    double? Points = null, string QuestionType = "");
public sealed record ExamQuestion(
    int Number,
    IReadOnlyList<ExamSegment> Body,
    string? Answer = null,
    string? Explanation = null,
    string? StudentAttempt = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyList<ExamPart>? Parts = null,
    double BlankMillimetres = 12,
    double? Points = null,
    string QuestionType = "");
public sealed record ExamPrintLabels(string Class = "Class", string Name = "Name", string StudentNumber = "Student no.",
    string Points = "({0:g} points)", string Answers = "Answers and explanations");
public sealed record ExamDocument(string Title, IReadOnlyList<ExamQuestion> Questions, ExamPrintLabels? Labels = null);

public sealed record TypesettingDiagnostic(int QuestionNumber, string Source, string Message);
public sealed record ExamExportResult(string PdfPath, string SourcePath, IReadOnlyList<TypesettingDiagnostic> Diagnostics);

public sealed record TypstToolchain(string ExecutablePath)
{
    public const string TextFontFamily = "NSimSun";
    public const string SansFontFamily = "Microsoft YaHei";
    public const string KaiFontFamily = "KaiTi";
    public const double BodySizePoints = 11;
    public const double FormulaPreviewMaximumWidthPoints = 560;
    public const string EngineVersion = "0.14.2";
    public const string ExecutableSha256 = "1bfcabe2756657587c5f879041b55265c2a954c2ba8125ada842d2a33b371695";
}
