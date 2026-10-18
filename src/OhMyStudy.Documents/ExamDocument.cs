namespace OhMyStudy.Documents;

public enum ExamPaper { A4Portrait, A3Landscape }
public enum ExamEdition { Questions, Answers }
/// <summary>Exactly one of text/formula, table rows, or a decoded raster image is present.</summary>
public sealed record ExamSegment(string Text, bool IsFormula = false, IReadOnlyList<IReadOnlyList<string>>? Rows = null, ExamImage? Image = null);
/// <summary>Raster bytes are copied into the export's private root; callers cannot supply a filesystem path.</summary>
public sealed record ExamImage(byte[] Bytes, string Caption = "", bool ContainsHandwriting = false);
public sealed record ExamPart(string Label, IReadOnlyList<ExamSegment> Body, string? Answer = null, double BlankMillimetres = 12,
    string? Explanation = null, IReadOnlyList<string>? Options = null, IReadOnlyList<ExamPart>? Parts = null);
public sealed record ExamQuestion(
    int Number,
    IReadOnlyList<ExamSegment> Body,
    string? Answer = null,
    string? Explanation = null,
    string? StudentAttempt = null,
    IReadOnlyList<string>? Options = null,
    IReadOnlyList<ExamPart>? Parts = null,
    double BlankMillimetres = 12);
public sealed record ExamDocument(string Title, IReadOnlyList<ExamQuestion> Questions);

public sealed record TypesettingDiagnostic(int QuestionNumber, string Source, string Message);
public sealed record ExamExportResult(string PdfPath, string SourcePath, IReadOnlyList<TypesettingDiagnostic> Diagnostics);
public sealed record FormulaPreviewResult(string PdfPath, string SvgPath, FormulaConversion Conversion);

/// <summary>Paths resolve to the private shipped native engine and a pinned font folder.</summary>
public sealed record TypstToolchain(string ExecutablePath, string FontDirectory)
{
    public const string TextFontFamily = "Source Han Serif SC";
    public const string MathFontFamily = "New Computer Modern Math";
    public const double BodySizePoints = 11;
    public const double FormulaPreviewMaximumWidthPoints = 560;
    public const string EngineVersion = "0.14.2";
    public const string ExecutableSha256 = "1bfcabe2756657587c5f879041b55265c2a954c2ba8125ada842d2a33b371695";
    public const string FontFileName = "SourceHanSerifSC-Regular.otf";
    public const string FontSha256 = "78aa7a328fd974df2d688c8a9fd74a33d8334dfa84ab24d9d11efb2ffc464117";
}
