using System.Globalization;
using System.Xml.Linq;
using OhMyStudy.Documents;

internal static class DocumentExportChecks
{
    internal static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("document_font_directives", () =>
        {
            string source = "{font:sans}甲 $x^2$ 乙{/font} {font:kai}丙{/font} {font:bad}丁{/font} {font:sans}未闭合";
            string normalized = DocumentText.NormalizeMath(source);
            Assert(normalized.Contains("{font:sans}", StringComparison.Ordinal));
            var runs = DocumentText.Split(normalized);
            Assert(runs.Any(run => run.IsFormula && run.Text == "x^2" && run.Font == DocumentFont.SansSerif));
            Assert(runs.Any(run => !run.IsFormula && run.Font == DocumentFont.Kai && run.Text.Contains('丙')));
            string literal = string.Join("", runs.Where(run => !run.IsFormula).Select(run => run.Text));
            Assert(literal.Contains("{font:bad}丁{/font}", StringComparison.Ordinal) && literal.Contains("{font:sans}未闭合", StringComparison.Ordinal));
            Assert(DocumentText.Split("plain").Single().Font == DocumentFont.Default);
            Assert(DocumentText.Split("{font:serif}serif{/font}").Single().Font == DocumentFont.Serif);
            Assert(DocumentText.Split("{font:sans-serif}alias{/font}").Single().Font == DocumentFont.SansSerif);
            return Task.CompletedTask;
        });
        await check("document_table_merges", () =>
        {
            var cells = DocumentTable.Layout([["ROOT", "<", "C"], ["^", "^", "D"], ["E"]]);
            Assert(cells.Count == 6);
            Assert(cells.Any(cell => cell.Text == "ROOT" && cell.Row == 0 && cell.Column == 0 && cell.RowSpan == 2 && cell.ColumnSpan == 2));
            Assert(cells.All(cell => cell.Text.Trim() is not ("^" or "<" or ">")));
            Assert(DocumentTable.Layout([[">", "RIGHT"]]).Single() is { Text: "RIGHT", ColumnSpan: 2 });
            Assert(DocumentTable.Layout([[" $x^2$ ", "x < y"]]).Count == 2);
            AssertInvalid(() => DocumentTable.Layout([["ROOT", "<"], ["^", "BAD"]]));
            return Task.CompletedTask;
        });
        await check("document_mermaid_svg", () =>
        {
            string svg = MermaidRenderer.RenderSvg("flowchart TD\nA[\"Start & <safe>\"] --> B(Choose)\nB -- yes --> C{Done}\nC --- A");
            Assert(svg.Contains("Start &amp; &lt;safe&gt;", StringComparison.Ordinal) && !svg.Contains("<safe>", StringComparison.Ordinal));
            Assert(svg.Contains("NSimSun", StringComparison.Ordinal) && svg.Contains("polygon", StringComparison.Ordinal));
            var xml = XElement.Parse(svg);
            Assert(double.Parse(xml.Attribute("width")!.Value.Replace("px", ""), CultureInfo.InvariantCulture) > 0);
            Assert(!xml.Descendants().Any(element => element.Name.LocalName == "script"));
            var display = MermaidRenderer.RenderDisplay("flowchart LR\nA[Start] --> B{Ready?}\nB -- yes --> C(Done)");
            var displayXml = XElement.Parse(display.Svg);
            Assert(display.Width > 0 && display.Height > 0 && display.Labels.Count == 4);
            Assert(display.Labels.Any(label => label.Text == "Ready?" && label.FontSize == 13 && label.Width > 0 && label.Height > 0));
            Assert(!displayXml.Descendants().Any(element => element.Name.LocalName is "text" or "marker"));
            Assert(displayXml.Descendants().Count(element => element.Name.LocalName == "polygon" && element.Attribute("fill")?.Value == "#444") == 2);
            foreach (string direction in new[] { "TD", "LR", "BT", "RL" }) Assert(MermaidRenderer.RenderSvg("graph " + direction + "; A-->B; B-->A").Contains("</svg>", StringComparison.Ordinal));
            foreach (string invalid in new[] { "sequenceDiagram\nA->B", "flowchart TD\nA -.-> B", "flowchart XX\nA-->B", "flowchart TD\nA[unclosed" })
                AssertInvalid(() => MermaidRenderer.RenderSvg(invalid));
            return Task.CompletedTask;
        });
        await check("document_visual_width_validation", () =>
        {
            Assert(new ExamImage([1, 2, 3, 4, 5, 6, 7, 8]).WidthPercent == 100);
            Assert(new ExamMermaid("graph TD; A").WidthPercent == 100);
            foreach (double width in new[] { 0, 101, double.NaN, double.PositiveInfinity })
            {
                bool rejected = false;
                try { _ = new ExamImage([1, 2, 3, 4, 5, 6, 7, 8], WidthPercent: width); }
                catch (ArgumentOutOfRangeException) { rejected = true; }
                Assert(rejected);
            }
            return Task.CompletedTask;
        });
    }

    internal static async Task<int> RunStandaloneAsync()
    {
        int failures = 0, count = 0;
        await RunAsync(async (name, action) =>
        {
            count++;
            try { await action(); Console.WriteLine("PASS " + name); }
            catch (Exception error) { failures++; Console.WriteLine("FAIL " + name + ": " + error); }
        });
        Console.WriteLine($"Document export checks: {count}; failures: {failures}");
        return failures == 0 ? 0 : 1;
    }

    private static void AssertInvalid(Action action)
    {
        bool rejected = false;
        try { action(); }
        catch (InvalidDataException) { rejected = true; }
        Assert(rejected);
    }

    private static void Assert(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Assertion failed");
    }
}
