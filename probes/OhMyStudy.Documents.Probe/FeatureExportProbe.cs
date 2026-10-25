using System.Globalization;
using System.Xml.Linq;
using OhMyStudy.Documents;

internal static class FeatureExportProbe
{
    public static async Task VerifyAsync(string root, string output, TypstDocumentService service, PdfiumRasterizer rasterizer, List<string> checks)
    {
        VerifyPureLayout();
        using var fixture = System.Text.Json.JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(root, "tests", "Fixtures", "Compatibility", "assets.synthetic.json")));
        byte[] png = Convert.FromBase64String(fixture.RootElement.GetProperty("assets")[0].GetProperty("base64").GetString()!);
        IReadOnlyList<IReadOnlyList<string>> merged =
        [
            ["MERGE_ROOT", "<", "MERGE_C"],
            ["^", "^", "MERGE_D"],
            ["MERGE_E"]
        ];
        IReadOnlyList<IReadOnlyList<string>> wide =
        [
            ["WIDE_00", "WIDE_01", "WIDE_02", "WIDE_03", "WIDE_04", "WIDE_05", "<", "WIDE_07"]
        ];
        var document = new ExamDocument("FONT_AND_FLOW_EXPORT", [new ExamQuestion(1,
        [
            new("SERIF_A {font:sans}SANS_A $x^2$ SANS_B{/font} {font:kai}KAI_A{/font} {font:serif}SERIF_B{/font}"),
            new("MALFORMED_{font:unknown}_TEXT {font:sans}UNCLOSED_FONT"),
            new("", Rows: merged),
            new("", Rows: wide),
            new("", Mermaid: new ExamMermaid("flowchart LR\nSTART[\"Flow & <start>\"] --> DECISION{Ready?}\nDECISION -- yes --> DONE(Done)\nDECISION -- no --> START", "MERMAID_CAPTION", 60)),
            new("", Image: new ExamImage(png, "IMAGE_NARROW", WidthPercent: 20)),
            new("", Image: new ExamImage(png, "IMAGE_WIDE", WidthPercent: 80)),
            new("", Image: new ExamImage(png, "IMAGE_FULL"))
        ], Answer: "{font:sans}ANSWER_SANS{/font}", Explanation: "{font:kai}EXPLANATION_KAI{/font}", Options: ["{font:sans}OPTION_SANS{/font}"])]);
        string target = Path.Combine(output, "feature-export");
        foreach (var paper in Enum.GetValues<ExamPaper>())
            foreach (var columns in new[] { ExamColumns.One, ExamColumns.Two, ExamColumns.Three, ExamColumns.Four })
            {
                int count = (int)columns;
                string directory = Path.Combine(target, (paper == ExamPaper.A4Portrait ? "a4-" : "a3-") + count);
                var result = await service.ExportExamAsync(document, paper, ExamEdition.Answers, directory, columns: columns);
                string source = await File.ReadAllTextAsync(result.SourcePath);
                Require(source.Contains("paper: \"" + (paper == ExamPaper.A4Portrait ? "a4" : "a3") + "\"", StringComparison.Ordinal), "Typst paper selection");
                Require(count == 1 && !source.Contains("#columns(", StringComparison.Ordinal)
                    || count > 1 && source.Contains("#columns(" + count + ", gutter: 12mm)", StringComparison.Ordinal), "independent Typst column count");
                foreach (string font in new[] { "NSimSun", "Microsoft YaHei", "KaiTi" }) Require(source.Contains("font: \"" + font + "\"", StringComparison.Ordinal), "run font " + font);
                Require(source.Contains("rowspan: 2, colspan: 2", StringComparison.Ordinal), "rectangular Typst merge");
                Require(source.Contains("Table: columns 1–5", StringComparison.Ordinal) && source.Contains("Table: columns 6–8", StringComparison.Ordinal), "wide table panel avoids crossing merge");
                double fullWidth = paper == ExamPaper.A4Portrait ? 176 : 386;
                foreach (double percent in new[] { 20, 60, 80, 100 })
                    Require(source.Contains("width: " + (fullWidth * percent / 100).ToString("0.###", CultureInfo.InvariantCulture) + "mm", StringComparison.Ordinal), "full-page-relative width " + percent);
                Require(result.Diagnostics.Count == 0, "feature export has no formula diagnostics");
                using var pdf = rasterizer.Open(result.PdfPath);
                Require(pdf.PageCount > 0, "feature PDF pages");
                for (int page = 0; page < pdf.PageCount; page++)
                {
                    var raster = pdf.RenderPage(page, 1200);
                    double expectedWidth = paper == ExamPaper.A4Portrait ? 210 : 420;
                    Require(Math.Abs(raster.PageWidthPoints * 25.4 / 72 - expectedWidth) < 0.05 && Math.Abs(raster.PageHeightPoints * 25.4 / 72 - 297) < 0.05, "feature PDF paper dimensions");
                    raster.SaveBmp(Path.Combine(directory, "page-" + (page + 1) + ".bmp"));
                }
                string text = string.Join("\n", Enumerable.Range(0, pdf.PageCount).Select(pdf.ExtractText)).Replace("​", "");
                foreach (string marker in new[] { "SERIF_A", "SANS_A", "SANS_B", "KAI_A", "SERIF_B", "MALFORMED_", "UNCLOSED_FONT",
                    "MERGE_ROOT", "MERGE_C", "MERGE_D", "MERGE_E", "WIDE_00", "WIDE_05", "WIDE_07", "MERMAID_CAPTION",
                    "IMAGE_NARROW", "IMAGE_WIDE", "IMAGE_FULL", "ANSWER_SANS", "EXPLANATION_KAI", "OPTION_SANS" })
                    Require(text.Contains(marker, StringComparison.Ordinal), "feature PDF retains " + marker);
                Require(!text.Contains("{font:sans}SANS_A", StringComparison.Ordinal) && !text.Contains("{font:kai}KAI_A", StringComparison.Ordinal), "valid directives are not printed");
                await File.WriteAllTextAsync(Path.Combine(directory, "extracted.txt"), text);
                string compact = text.Replace("\r", "", StringComparison.Ordinal).Replace("\n", "", StringComparison.Ordinal);
                Require(compact.Contains("{font:unknown}", StringComparison.Ordinal) && compact.Contains("{font:sans}UNCLOSED_FONT", StringComparison.Ordinal), "malformed directives remain literal");
            }
        checks.Add("A4/A3 with independent 1/2/3/4 columns: eight compiled and rasterized PDFs; source page/column settings and actual paper dimensions");
        checks.Add("PDF fonts, formula-safe directives, malformed literal directives, rectangular/ragged/wide merged tables, Mermaid SVG, and 20/60/80/100 percent full-page visual widths");
    }

    private static void VerifyPureLayout()
    {
        string source = "{font:sans}甲 $x^2$ 乙{/font} {font:kai}丙{/font} {font:bad}丁{/font} {font:sans}未闭合";
        string normalized = DocumentText.NormalizeMath(source);
        Require(normalized.Contains("{font:sans}", StringComparison.Ordinal) && normalized.Contains("$x^2$", StringComparison.Ordinal), "NormalizeMath preserves font directives");
        var runs = DocumentText.Split(normalized);
        Require(runs.Any(run => run.IsFormula && run.Font == DocumentFont.SansSerif && run.Text == "x^2"), "formula retains surrounding font without changing math");
        Require(runs.Any(run => !run.IsFormula && run.Font == DocumentFont.SansSerif && run.Text.Contains("甲", StringComparison.Ordinal)), "sans run");
        Require(runs.Any(run => !run.IsFormula && run.Font == DocumentFont.Kai && run.Text.Contains("丙", StringComparison.Ordinal)), "kai run");
        string literal = string.Join("", runs.Where(run => !run.IsFormula).Select(run => run.Text));
        Require(literal.Contains("{font:bad}丁{/font}", StringComparison.Ordinal) && literal.Contains("{font:sans}未闭合", StringComparison.Ordinal), "malformed directives remain literal: " + literal);
        Require(DocumentText.Split("plain").Single().Font == DocumentFont.Default, "default run");
        Require(DocumentText.Split("{font:serif}serif{/font}").Single().Font == DocumentFont.Serif, "explicit serif run");
        Require(DocumentText.Split("{font:sans-serif}alias{/font}").Single().Font == DocumentFont.SansSerif, "sans-serif alias");
        var cells = DocumentTable.Layout([["ROOT", "<", "C"], ["^", "^", "D"], ["E"]]);
        Require(cells.Any(cell => cell.Text == "ROOT" && cell.RowSpan == 2 && cell.ColumnSpan == 2), "shared rectangular merge layout");
        Require(cells.Count == 6 && cells.All(cell => cell.Text.Trim() is not ("^" or "<" or ">")), "merge markers and ragged blanks");
        Require(DocumentTable.Layout([[">", "RIGHT"]]).Single().Text == "RIGHT", "rightward merge");
        Require(DocumentTable.Layout([[" $x^2$ ", "x < y"]]).Count == 2, "markers embedded in prose and formulas stay literal");
        try { DocumentTable.Layout([["ROOT", "<"], ["^", "BAD"]]); throw new InvalidOperationException("non-rectangular merge accepted"); }
        catch (InvalidDataException) { }
        string svg = MermaidRenderer.RenderSvg("flowchart TD\nA[\"Start & <safe>\"] --> B(Choose)\nB -- yes --> C{Done}\nC --- A");
        Require(svg.Contains("Start &amp; &lt;safe&gt;", StringComparison.Ordinal) && !svg.Contains("<safe>", StringComparison.Ordinal), "Mermaid XML escaping");
        Require(svg.Contains("NSimSun", StringComparison.Ordinal) && svg.Contains("polygon", StringComparison.Ordinal) && svg.Contains("fill=\"#444\"", StringComparison.Ordinal)
            && !svg.Contains("marker-end", StringComparison.Ordinal), "Mermaid shapes, explicit arrow geometry and font");
        var xml = XElement.Parse(svg);
        Require(double.Parse(xml.Attribute("width")!.Value.Replace("px", ""), CultureInfo.InvariantCulture) > 0
            && double.Parse(xml.Attribute("height")!.Value.Replace("px", ""), CultureInfo.InvariantCulture) > 0
            && !xml.Descendants().Any(element => element.Name.LocalName == "script"), "safe bounded SVG dimensions");
        var display = MermaidRenderer.RenderDisplay("flowchart LR\rA[Start] --> B{Ready?}\rB -- yes --> C(Done)");
        var displayXml = XElement.Parse(display.Svg);
        Require(display.Width > 0 && display.Height > 0 && display.Labels.Count == 4, "Mermaid display dimensions and node/edge labels");
        Require(display.Labels.Any(label => label.Text == "Ready?" && label.FontSize == 13 && label.Width > 0 && label.Height > 0)
            && display.Labels.Any(label => label.Text == "yes" && label.FontSize == 12), "Mermaid display label text and sizing");
        Require(!displayXml.Descendants().Any(element => element.Name.LocalName is "text" or "marker")
            && displayXml.Descendants().Count(element => element.Name.LocalName == "polygon" && element.Attribute("fill")?.Value == "#444") == 2, "text-free WinUI SVG with explicit arrows");
        foreach (string direction in new[] { "TD", "LR", "BT", "RL" }) Require(MermaidRenderer.RenderSvg("graph " + direction + "; A-->B; B-->A; A-->A").Contains("</svg>", StringComparison.Ordinal), "finite Mermaid direction/cycle layout");
        foreach (string invalid in new[] { "sequenceDiagram\nA->B", "flowchart TD\nA -.-> B", "flowchart XX\nA-->B", "flowchart TD\nA[unclosed" })
            try { MermaidRenderer.RenderSvg(invalid); throw new InvalidOperationException("invalid Mermaid accepted: " + invalid); }
            catch (InvalidDataException error) { Require(error.Message.Contains("Mermaid", StringComparison.Ordinal), "actionable Mermaid error"); }
        foreach (double width in new[] { 0, 101, double.NaN, double.PositiveInfinity })
            try { _ = new ExamImage([1, 2, 3, 4, 5, 6, 7, 8], WidthPercent: width); throw new InvalidOperationException("invalid image width accepted"); }
            catch (ArgumentOutOfRangeException) { }
        Require(new ExamImage([1, 2, 3, 4, 5, 6, 7, 8]).WidthPercent == 100 && new ExamMermaid("graph TD; A").WidthPercent == 100, "default visual widths");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}
