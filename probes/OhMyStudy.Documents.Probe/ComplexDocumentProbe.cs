using System.Text.Json;
using OhMyStudy.Documents;

internal static class ComplexDocumentProbe
{
    public static async Task VerifyAsync(string root, string output, TypstDocumentService service, PdfiumRasterizer rasterizer, List<string> checks)
    {
        string target = Path.Combine(output, "complex");
        using var fixture = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests", "Fixtures", "Compatibility", "assets.synthetic.json")));
        byte[] png = Convert.FromBase64String(fixture.RootElement.GetProperty("assets")[0].GetProperty("base64").GetString()!);
        var table = Enumerable.Range(0, 65).Select(row => (IReadOnlyList<string>)Enumerable.Range(0, row == 10 ? 7 : 8)
            .Select(column => $"CELL_{row:00}_{column:00}").ToArray()).ToArray();
        var leaf = new ExamPart("L12", [new("DEEP_BODY"), new("", Rows: [["深层表格", "$x^2$"]])],
            "DEEP_ANSWER_PRIVATE", Explanation: "DEEP_EXPLANATION_PRIVATE", Options: ["DEEP_OPTION"]);
        for (int depth = 11; depth >= 1; depth--) leaf = new ExamPart("L" + depth, [new("层级 " + depth)],
            "LEVEL_ANSWER_PRIVATE_" + depth, Parts: [leaf]);
        string longText = string.Join("\n", Enumerable.Range(0, 100).Select(index => $"段落 {index}：分析图中的自变量和因变量，完整说明推理依据与结论。本段应随页面换行，不能裁切。"));
        string unsupported = @"\futurecommand{" + new string('x', 9000) + " UNSUPPORTED_TAIL}";
        var document = new ExamDocument("复杂内容验证示例", [new ExamQuestion(1,
            [new(longText + " LONG_BODY_TAIL"), new("", Rows: table),
             new("", Image: new ExamImage(png, "IMAGE_CAPTION #panic(\"LITERAL_ONLY\")")), new(unsupported, true)],
            "ROOT_ANSWER_PRIVATE", "ROOT_EXPLANATION_PRIVATE", "STUDENT_PRIVATE_COMPLEX",
            Options: [@"A. $\frac{1}{2}$", @"B. \(x^2\)"], Parts: [leaf])]);
        foreach (var paper in Enum.GetValues<ExamPaper>())
            foreach (var edition in Enum.GetValues<ExamEdition>())
            {
                var result = await service.ExportExamAsync(document, paper, edition, target);
                Require(result.Diagnostics.Count == 1, "unsupported formula retained with one diagnostic");
                using var pdf = rasterizer.Open(result.PdfPath);
                Require(pdf.PageCount >= 3, "complex content paginates");
                string text = string.Join("\n", Enumerable.Range(0, pdf.PageCount).Select(page => pdf.ExtractText(page))).Replace("\u200b", "");
                foreach (var marker in new[] { "LONG_BODY_TAIL", "DEEP_BODY", "DEEP_OPTION", "IMAGE_CAPTION", "UNSUPPORTED_TAIL", "LITERAL_ONLY" })
                    Require(text.Contains(marker, StringComparison.Ordinal), "retained " + marker);
                foreach (var row in table) foreach (var cell in row) Require(text.Contains(cell, StringComparison.Ordinal), "retained " + cell);
                foreach (var marker in new[] { "ROOT_ANSWER_PRIVATE", "ROOT_EXPLANATION_PRIVATE", "DEEP_ANSWER_PRIVATE", "DEEP_EXPLANATION_PRIVATE", "LEVEL_ANSWER_PRIVATE_11" })
                    Require(text.Contains(marker, StringComparison.Ordinal) == (edition == ExamEdition.Answers), "edition isolation " + marker);
                Require(!text.Contains("STUDENT_PRIVATE", StringComparison.Ordinal), "student attempt isolation");
                await File.WriteAllTextAsync(Path.ChangeExtension(result.PdfPath, ".txt"), text);
                for (int page = 0; page < pdf.PageCount; page++)
                    pdf.RenderPage(page, 1400).SaveBmp(Path.Combine(target, Path.GetFileNameWithoutExtension(result.PdfPath) + "-" + (page + 1) + ".bmp"));
            }
        var handwriting = new ExamDocument("手写题图", [new ExamQuestion(1, [new("", Image: new ExamImage(png, ContainsHandwriting: true))])]);
        try
        {
            await service.ExportExamAsync(handwriting, ExamPaper.A4Portrait, ExamEdition.Questions, Path.Combine(target, "rejected-handwriting"));
            throw new InvalidOperationException("handwritten image entered question edition");
        }
        catch (InvalidDataException error) when (error.Message.Contains("handwriting", StringComparison.Ordinal)) { }
        Require(DocumentText.Split(@"甲 $x^2$ 乙 \(y\) 丙 \[z\] 丁 $$a+b$$").Count(run => run.IsFormula) == 4, "shared formula boundaries");
        Require(DocumentText.Split("unclosed $x").All(run => !run.IsFormula), "unclosed delimiter retained as text");
        checks.Add("complex A3/A4 PDF: 519 table cells, ragged/wide/paginated tables, raster image, 12-level parts, long body, full 9000-character formula fallback; actual extracted text and edition isolation");
        checks.Add("handwritten question image rejected; all formula delimiters share preview/export boundaries");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
