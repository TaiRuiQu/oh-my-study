using System.Xml.Linq;
using OhMyStudy.Documents;

internal static class ReadingProbe
{
    internal static async Task VerifyAsync(TypstDocumentService service, string output)
    {
        Directory.CreateDirectory(output);
        string[] formulas = [@"\frac{x^2+1}{\sqrt{x+2}}", @"\begin{pmatrix}1&2\\3&4\end{pmatrix}",
            @"f(x)=\begin{cases}x^2&x\ge0\\-x&x<0\end{cases}", @"\text{平均速度}=\frac{\text{总路程}}{\text{总时间}}",
            @"\cancel{x}+\boxed{y}", @"\boldsymbol{a}\cdot\boldsymbol{b}", @"\frac12", @"\displaystyle\sum_{k=1}^n k"];
        for (int index = 0; index < formulas.Length; index++)
        {
            Require(VelociTexFormulaRenderer.TryWriteSvg(formulas[index], Path.Combine(output, $"formula-{index}.svg"), 11, true,
                CancellationToken.None, out var result, out var diagnostic), "VelociTex must compile " + formulas[index] + ": " + diagnostic);
            var svg = XElement.Load(result!.Path);
            Require(result.WidthPoints > 0 && result.HeightPoints > 0 && result.BaselinePoints > 0, "finite nonempty formula bounds");
            Require(svg.Descendants().Any(element => element.Name.LocalName == "path"), "formula contains actual glyph outlines");
            Require(!svg.Descendants().Any(element => element.Name.LocalName is "text" or "script" or "image"), "formula SVG is self contained");
        }
        Require(!VelociTexFormulaRenderer.TryWriteSvg(@"\unsupported{a}", Path.Combine(output, "invalid.svg"), 11, true,
            CancellationToken.None, out var invalid, out var error) && invalid is null && !string.IsNullOrEmpty(error), "invalid formula returns diagnostic");
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        try
        {
            VelociTexFormulaRenderer.TryWriteSvg("x", Path.Combine(output, "cancelled.svg"), 11, true, cancelled.Token, out _, out _);
            throw new InvalidOperationException("cancelled compilation did not stop");
        }
        catch (OperationCanceledException) { }
        Require(DocumentText.Split(@"甲 $x$ 乙 \(y\) 丙 \[z\] 丁 $$a$$").Count(run => run.IsFormula) == 4, "four delimiters");
        Require(DocumentText.Split(DocumentText.NormalizeMath(@"A. \cancel{x}")).Any(run => run.IsFormula), "new commands bypass retired Typst whitelist");
        Require(DocumentText.NormalizeMath("A. 充分不必要条件") == "A. 充分不必要条件", "ordinary Chinese option stays prose");
        Require(DocumentText.Split(@"费用 \$5，未闭合 \(x").All(run => !run.IsFormula), "escaped and unclosed delimiters stay prose");
        var document = new ExamDocument("公式打印示例", [new ExamQuestion(1,
            [new(@"行内公式 $\frac{x^2+1}{2}$ 与正文对齐。\[\cancel{x}+\boxed{y}\]"),
             new(@"\text{平均速度}=\frac{\text{总路程}}{\text{总时间}}", true),
             new("", Rows: [["函数", "表达式"], ["根式", @"$\sqrt{x+2}$"]])],
            Answer: @"$\boxed{y}$", Explanation: @"由 $x=2$ 得到 $y=\frac12$。", Options: [@"A. \cancel{x}", @"B. $\sqrt{2}$"],
            Parts: [new ExamPart("(1)", [new(@"计算 $\sum_{k=1}^n k$。")])])]);
        foreach (var paper in Enum.GetValues<ExamPaper>())
            foreach (var edition in Enum.GetValues<ExamEdition>())
            {
                var export = await service.ExportExamAsync(document, paper, edition, output);
                Require(export.Diagnostics.Count == 0, "all PDF formula routes use VelociTex");
                string source = await File.ReadAllTextAsync(export.SourcePath);
                Require(source.Contains("formula-", StringComparison.Ordinal) && !source.Contains("math.equation", StringComparison.Ordinal)
                    && !source.Contains("New Computer Modern", StringComparison.Ordinal), "PDF embeds outlines without Typst mathematics or retired font");
            }
        await File.WriteAllTextAsync(Path.Combine(output, "reading-checks.txt"), "PASS: 8 VelociTex formula SVGs, CJK, new commands, diagnostics, cancellation, boundaries; all four A3/A4 question/answer exports use formula outlines.");
    }

    private static void Require(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
}
