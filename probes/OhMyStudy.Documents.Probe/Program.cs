using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using OhMyStudy.Documents;

var root = Path.GetFullPath(args.ElementAtOrDefault(0) ?? Directory.GetCurrentDirectory());
var output = Path.GetFullPath(args.ElementAtOrDefault(1) ?? Path.Combine(root, "output", "migration", "documents"));
Directory.CreateDirectory(output);
var clock = Stopwatch.StartNew();
var toolchain = new TypstToolchain(Path.Combine(root, "output", "migration", "toolchain", "typst-x86_64-pc-windows-msvc", "typst.exe"), Path.Combine(root, "output", "migration", "toolchain", "fonts"));
var pdfium = new PdfiumRasterizer(Path.Combine(root, "output", "migration", "toolchain", "pdfium", "pdfium.dll"));
var typesetter = new TypstDocumentService(toolchain);
var samples = new[]
{
    @"\frac{x^2-1}{x-1}+\frac{1}{\sqrt{x+2}}",
    @"A=\begin{pmatrix}1&2&3\\0&-1&4\\2&0&1\end{pmatrix}",
    @"f(x)=x_1^2+e^{-x}+\sum_{k=1}^{n}k^2",
    @"\text{平均速度}=\frac{\text{总路程}}{\text{总时间}}",
    @"\ce{2H2 + O2 ->[\text{点燃}] 2H2O}",
    @"\ce{SO4^2- + Ba^2+ -> BaSO4 v}",
    @"\ce{Fe^3+ + 3OH^- -> Fe(OH)3 v}",
    @"\ce{N2 + 3H2 <=>[高温高压][催化剂] 2NH3}",
    string.Join("+", Enumerable.Range(1, 60).Select(index => "x_{" + index + "}^2"))
};
var checks = new List<string>();
foreach (var sample in samples) Require(LatexFormulaConverter.Convert(sample).IsSupported, "supported formula: " + sample);
checks.Add("9 representative mathematics/chemistry/long-formula samples parsed");
foreach (var bad in new[] { @"\href{https://example.invalid}{x}", @"\frac{1}", @"\ce{H2 ->[bad}", "#panic(\"UNTRUSTED\")", "x_", "a(b", "a)b", "{}", new string('{', 64) + "x" + new string('}', 64), string.Concat(Enumerable.Repeat(@"\sqrt", 64)) + "x", new string('x', 8193) })
    Require(!LatexFormulaConverter.Convert(bad).IsSupported, "invalid input rejected");
checks.Add("11 invalid/unsafe/deep/oversized inputs return readable diagnostic");
var operators = @"\prod + \int + \iint + \infty + \cdot + \times + \div + \pm + \mp + \leq + \geq + \neq + \approx + \equiv + \in + \notin + \subset + \cup + \cap + \to + \leftarrow + \leftrightarrow + \Rightarrow + \Leftrightarrow + \uparrow + \downarrow + \ldots + \sin + \cos + \tan + \ln + \log + \lim + \max + \min + \det + \alpha + \beta + \gamma + \delta + \epsilon + \theta + \lambda + \mu + \nu + \xi + \pi + \rho + \sigma + \tau + \phi + \chi + \psi + \omega + \Delta + \Sigma + \Omega + \Gamma + \Theta + \Phi + \left(x+1\right)^2 + \sqrt[3]{x} + \overline{x} + \vec{x} + \mathbf{x} + \mathit{x} + \mathrm{ABC} + \quad + \qquad";
// Separate short lines keep this coverage compilation below the preview fallback threshold.
foreach (var (formula, index) in operators.Split(" + ").Select((formula, index) => (formula, index)))
    await typesetter.RenderFormulaAsync(formula, Path.Combine(output, "operator-" + index));
checks.Add("all additional whitelisted operators and delimiter/root/style forms compiled");
for (var index = 0; index < samples.Length; index++)
{
    var preview = await typesetter.RenderFormulaAsync(samples[index], Path.Combine(output, "formula-" + (index + 1)));
    using var pdf = pdfium.Open(preview.PdfPath);
    Require(pdf.PageCount == 1, "single page formula preview");
    pdf.RenderPage(0, 1000).SaveBmp(Path.Combine(Path.GetDirectoryName(preview.PdfPath)!, "formula.bmp"));
    var svg = await File.ReadAllTextAsync(preview.SvgPath);
    Require(!svg.Contains("<text", StringComparison.OrdinalIgnoreCase) && !svg.Contains("<script", StringComparison.OrdinalIgnoreCase), "self-contained glyph paths");
}
var quotedPreview = await typesetter.RenderFormulaAsync(@"\text{#panic(""UNTRUSTED_FORMULA"")}", Path.Combine(output, "formula-quoted"));
using (var pdf = pdfium.Open(quotedPreview.PdfPath)) Require(pdf.ExtractText(0).Contains("UNTRUSTED_FORMULA", StringComparison.Ordinal), "formula text injection remains text");
var invalidPreview = await typesetter.RenderFormulaAsync(@"\unsupported{a}", Path.Combine(output, "formula-invalid"));
using (var pdf = pdfium.Open(invalidPreview.PdfPath))
{
    Require(pdf.ExtractText(0).Contains("unsupported", StringComparison.Ordinal), "invalid preview preserves original");
    pdf.RenderPage(0, 1000).SaveBmp(Path.Combine(output, "formula-invalid", "formula.bmp"));
}
checks.Add("9 representative formulas, quoted formula text and 1 readable error compiled to native PDF/SVG and rasterized by PDFium");

var questions = samples.Select((formula, index) => new ExamQuestion(index + 1,
    new ExamSegment[] { new("阅读公式并说明各符号与条件："), new(formula, true) },
    "ANSWER_ONLY_" + (index + 1) + "：此处为原创样题答案，仅用于迁移验证。",
    "结合题干条件解释各项关系，检查单位与适用范围。",
    "STUDENT_PRIVATE_" + (index + 1),
    index == 0 ? new[] { "A. 注意定义域", "B. 检查反应条件", "C. 保留单位", "D. 明确结论" } : null,
    index == 8 ? Enumerable.Range(1, 12).Select(part => new ExamPart("（" + part + "）", new[] { new ExamSegment("在相同实验条件下记录自变量与因变量，分析误差来源，给出推理过程并解释结论。必须联系题干中的限制条件，说明结论成立的范围。") }, "SUBANSWER_" + part + "：保持其他变量相同，以重复实验比较变化趋势。", 17)).ToArray() : null,
    15)).ToList();
questions.Add(new(10, new[] { new ExamSegment("以下内容必须只作为普通文字出现：#panic(\"UNTRUSTED_TYPST\")。"), new ExamSegment(@"\unsupported{not-renderable}", true) }, "ANSWER_ONLY_10：输入保留原文。", StudentAttempt: "STUDENT_PRIVATE_10"));
var document = new ExamDocument("数学与化学 · 原生排版验证卷", questions);
var oversizedLabel = new ExamDocument("bounded-input", new[]
{
    new ExamQuestion(1, new[] { new ExamSegment("合成题干") }, Parts: new[]
    {
        new ExamPart(new string('x', 32769), new[] { new ExamSegment("合成小问") })
    })
});
try
{
    await typesetter.ExportExamAsync(oversizedLabel, ExamPaper.A4Portrait, ExamEdition.Questions, Path.Combine(output, "rejected-label"));
    throw new InvalidOperationException("oversized part label bypassed template budget");
}
catch (ArgumentException) { }
checks.Add("oversized question-part label rejected before template write or native engine launch");
var exports = new List<ExportEvidence>();
foreach (var paper in Enum.GetValues<ExamPaper>())
    foreach (var edition in Enum.GetValues<ExamEdition>())
    {
        var result = await typesetter.ExportExamAsync(document, paper, edition, output);
        var source = await File.ReadAllTextAsync(result.SourcePath);
        Require(!source.Contains("STUDENT_PRIVATE", StringComparison.Ordinal), "student attempt absent from all outputs");
        if (edition == ExamEdition.Questions) Require(!source.Contains("ANSWER_ONLY", StringComparison.Ordinal) && !source.Contains("SUBANSWER", StringComparison.Ordinal), "question source answer isolation");
        using var pdf = pdfium.Open(result.PdfPath);
        var texts = new List<string>();
        double width = 0, height = 0;
        for (var page = 0; page < pdf.PageCount; page++)
        {
            var raster = pdf.RenderPage(page, 1400);
            width = raster.PageWidthPoints * 25.4 / 72;
            height = raster.PageHeightPoints * 25.4 / 72;
            Require(Math.Abs(width - (paper == ExamPaper.A3Landscape ? 420 : 210)) < 0.05 && Math.Abs(height - 297) < 0.05, "actual PDF paper dimensions");
            raster.SaveBmp(Path.Combine(output, Path.GetFileNameWithoutExtension(result.PdfPath) + "-" + (page + 1) + ".bmp"));
            texts.Add(pdf.ExtractText(page));
        }
        var text = string.Join("\n", texts);
        foreach (var stale in Directory.EnumerateFiles(output, Path.GetFileNameWithoutExtension(result.PdfPath) + "-*.bmp"))
        {
            var suffix = Path.GetFileNameWithoutExtension(stale)[(Path.GetFileNameWithoutExtension(result.PdfPath).Length + 1)..];
            if (int.TryParse(suffix, out var number) && number > pdf.PageCount) File.Delete(stale);
        }
        await File.WriteAllTextAsync(Path.ChangeExtension(result.PdfPath, ".txt"), text);
        Require(!text.Contains("STUDENT_PRIVATE", StringComparison.Ordinal), "actual PDF student isolation");
        if (edition == ExamEdition.Questions)
        {
            Require(!text.Contains("ANSWER_ONLY", StringComparison.Ordinal) && !text.Contains("SUBANSWER", StringComparison.Ordinal), "actual PDF answer isolation");
            Require(text.Contains("公式超过栏宽", StringComparison.Ordinal), "long formula readable fallback");
        }
        else
        {
            for (var index = 1; index <= questions.Count; index++) Require(text.Contains("ANSWER_ONLY_" + index, StringComparison.Ordinal), "answers retained");
            for (var index = 1; index <= 12; index++) Require(text.Contains("SUBANSWER_" + index, StringComparison.Ordinal), "part answers retained");
        }
        Require(!text.Contains('\ufffd'), "no Unicode replacement characters");
        exports.Add(new(Path.GetFileName(result.PdfPath), pdf.PageCount, new FileInfo(result.PdfPath).Length, width, height, result.Diagnostics.Count));
    }
checks.Add("A3 landscape/A4 portrait: actual PDF dimensions, page raster, question/answer/student isolation, all question and part answers");
using (var pdf = pdfium.Open(exports.Count > 0 ? Path.Combine(output, exports[0].File) : ""))
{
    var normal = pdf.RenderPage(0, 800);
    var rotated = pdf.RenderPage(0, 800, 1);
    Require(normal.Width == rotated.Height && normal.Height == rotated.Width, "quarter-turn raster dimensions");
    using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
    try { pdf.RenderPage(0, 800, cancellationToken: cancelled.Token); throw new Exception("cancel was ignored"); } catch (OperationCanceledException) { }
}
checks.Add("raster rotation, disposal and pre-cancellation");
var report = new ProbeReport(DateTimeOffset.Now, RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription,
    RuntimeFeature.IsDynamicCodeSupported, TypstToolchain.EngineVersion, clock.ElapsedMilliseconds, checks, exports);
await File.WriteAllTextAsync(Path.Combine(output, "verification.json"), JsonSerializer.Serialize(report, ProbeJsonContext.Default.ProbeReport));
Console.WriteLine("PASS native document probe; dynamic code=" + RuntimeFeature.IsDynamicCodeSupported + "; " + exports.Count + " PDFs; " + clock.ElapsedMilliseconds + " ms; " + output);

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }

internal sealed record ExportEvidence(string File, int Pages, long Bytes, double WidthMillimetres, double HeightMillimetres, int FormulaDiagnostics);
internal sealed record ProbeReport(DateTimeOffset Timestamp, string OperatingSystem, string Framework, bool DynamicCodeSupported, string TypstVersion, long ElapsedMilliseconds, List<string> Checks, List<ExportEvidence> Exports);
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(ProbeReport))]
internal partial class ProbeJsonContext : JsonSerializerContext;
