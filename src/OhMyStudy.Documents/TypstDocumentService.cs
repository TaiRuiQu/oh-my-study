using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OhMyStudy.Documents;

/// <summary>Generates only trusted templates. User text never becomes Typst source code.</summary>
public sealed class TypstDocumentService
{
    private readonly string executable;
    private readonly string fonts;
    private static string Quote(string value) => LatexFormulaConverter.Quote(value);

    public TypstDocumentService(TypstToolchain toolchain)
    {
        executable = Path.GetFullPath(toolchain.ExecutablePath);
        fonts = Path.GetFullPath(toolchain.FontDirectory);
        VerifyHash(executable, TypstToolchain.ExecutableSha256, "Typst");
        VerifyHash(Path.Combine(fonts, TypstToolchain.FontFileName), TypstToolchain.FontSha256, "字体");
    }

    private static void VerifyHash(string path, string expected, string name)
    {
        using var input = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(input));
        if (hash != expected) throw new InvalidDataException(name + "哈希不匹配，请运行原生文档工具准备脚本。 ");
    }

    public async Task<FormulaPreviewResult> RenderFormulaAsync(string latex, string outputDirectory, CancellationToken cancellationToken = default)
    {
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var conversion = LatexFormulaConverter.Convert(latex);
        var sourcePath = Path.Combine(directory, "formula.typ");
        var source = "#set page(width: auto, height: auto, margin: 3pt, fill: none)\n" + FontTemplate
            + (conversion.IsSupported ? "#context { let eq = $ " + conversion.TypstMath + " $\n if measure(eq).width > 560pt {\nblock(width: 560pt)[#text(size: 9pt, fill: rgb(\"785b18\"), \"公式超过预览宽度，原式如下：\") #linebreak() #text(" + Quote(conversion.Original) + ")]\n} else { eq } }"
                : "#block(width: 320pt)[#text(fill: rgb(\"9c3229\"), " + Quote(conversion.Diagnostic!) + ") #linebreak() #text(" + Quote(conversion.Original) + ")]\n");
        await File.WriteAllTextAsync(sourcePath, source, new UTF8Encoding(false), cancellationToken);
        var pdf = Path.Combine(directory, "formula.pdf");
        var svg = Path.Combine(directory, "formula.svg");
        await CompileAsync(sourcePath, pdf, cancellationToken);
        await CompileAsync(sourcePath, svg, cancellationToken);
        return new(pdf, svg, conversion);
    }

    public async Task<ExamExportResult> ExportExamAsync(ExamDocument document, ExamPaper paper, ExamEdition edition,
        string outputDirectory, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Questions.Count is 0 or > 500) throw new ArgumentOutOfRangeException(nameof(document), "题数必须为 1 至 500。 ");
        if (document.Title.Length > 512) throw new ArgumentException("标题过长。 ", nameof(document));
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var name = (paper == ExamPaper.A3Landscape ? "a3-" : "a4-") + (edition == ExamEdition.Questions ? "questions" : "answers");
        var sourcePath = Path.Combine(directory, name + ".typ");
        var diagnostics = new List<TypesettingDiagnostic>();
        var source = new StringBuilder("// Fixed native document template; no eval, import or user-selected paths.\n");
        source.Append(FontTemplate);
        source.Append("#set document(title: ").Append(Quote(document.Title)).Append(", author: \"Oh My Study\")\n");
        source.Append("#set page(paper: \"").Append(paper == ExamPaper.A3Landscape ? "a3" : "a4")
            .Append("\", flipped: ").Append(paper == ExamPaper.A3Landscape ? "true" : "false")
            .Append(", margin: (x: 17mm, top: 18mm, bottom: 17mm), footer: context align(center, text(size: 9pt)[#counter(page).display(\"1 / 1\", both: true)]))\n");
        source.Append("#set par(leading: 0.75em, spacing: 0.65em)\n");
        source.Append("#let fit-formula(body, original) = layout(size => {\n let eq = math.equation(block: true, body)\n if measure(eq).width > size.width {\n block(stroke: 0.4pt + rgb(\"b9a97b\"), inset: 5pt, width: 100%)[#text(size: 9pt, fill: rgb(\"785b18\"), \"公式超过栏宽，原式如下：\") #linebreak() #text(original)]\n } else { eq }\n})\n");
        source.Append("#align(center, text(size: 18pt, weight: \"bold\", ").Append(Quote(document.Title)).Append("))\n");
        source.Append("#align(center, text(size: 10pt, ").Append(Quote(edition == ExamEdition.Questions ? "题目卷 · 姓名：____________  日期：____________" : "答案解析册")).Append("))\n#v(4mm)\n");
        if (paper == ExamPaper.A3Landscape) source.Append("#columns(2, gutter: 12mm)[\n");
        long totalText = document.Title.Length;
        foreach (var question in document.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (question.Number < 1) throw new ArgumentException("题号必须为正整数。 ", nameof(document));
            source.Append("#block(breakable: false, above: 3mm, below: 1mm)[#strong[").Append(question.Number).Append(".] ");
            AppendSegments(source, question.Body, diagnostics, question.Number, ref totalText);
            source.Append("]\n");
            if (edition == ExamEdition.Answers)
            {
                // Explicit branch: question-edition source never receives answer/explanation/student fields.
                AppendTextBlock(source, "答案：" + (question.Answer ?? "未提供"), ref totalText);
                if (question.Explanation is { Length: > 0 }) AppendTextBlock(source, "解析：" + question.Explanation, ref totalText);
            }
            else if (question.Options is not null)
            {
                if (question.Options.Count > 16) throw new ArgumentException("选项数过多。 ", nameof(document));
                source.Append("#block(breakable: false)[\n");
                foreach (var option in question.Options) AppendTextBlock(source, option, ref totalText);
                source.Append("]\n");
            }
            if (question.Parts is not null)
            {
                if (question.Parts.Count > 100) throw new ArgumentException("小问过多。 ", nameof(document));
                foreach (var part in question.Parts)
                {
                    CountText(part.Label, ref totalText);
                    source.Append("#block(breakable: false, above: 2mm)[#text(")
                        .Append(Quote(question.Number.ToString(CultureInfo.InvariantCulture) + part.Label)).Append(") ");
                    if (edition == ExamEdition.Answers)
                        AppendTextBlock(source, part.Answer ?? "未提供", ref totalText);
                    else
                    {
                        AppendSegments(source, part.Body, diagnostics, question.Number, ref totalText);
                        AppendBlank(source, part.BlankMillimetres);
                    }
                    source.Append("]\n");
                }
            }
            if (edition == ExamEdition.Questions) AppendBlank(source, question.BlankMillimetres);
        }
        if (paper == ExamPaper.A3Landscape) source.Append("]\n");
        if (totalText > 1_000_000) throw new ArgumentException("试卷文字超过限制。 ", nameof(document));
        await File.WriteAllTextAsync(sourcePath, source.ToString(), new UTF8Encoding(false), cancellationToken);
        var pdf = Path.Combine(directory, name + ".pdf");
        await CompileAsync(sourcePath, pdf, cancellationToken);
        return new(pdf, sourcePath, diagnostics);
    }

    private static void AppendBlank(StringBuilder source, double millimetres)
    {
        if (!double.IsFinite(millimetres) || millimetres < 0 || millimetres > 100) throw new ArgumentOutOfRangeException(nameof(millimetres));
        source.Append("\n#v(").Append(millimetres.ToString(CultureInfo.InvariantCulture)).Append("mm)\n");
    }

    private static void CountText(string text, ref long total)
    {
        if (text.Length > 32_768) throw new ArgumentException("单段文字超过 32768 字符。 ", nameof(text));
        total += text.Length;
        if (total > 1_000_000) throw new ArgumentException("试卷文字超过限制。 ", nameof(text));
    }

    private static void AppendTextBlock(StringBuilder source, string text, ref long total)
    {
        CountText(text, ref total);
        source.Append("#block(text(").Append(Quote(text)).Append("))\n");
    }

    private static void AppendSegments(StringBuilder source, IReadOnlyList<ExamSegment> segments,
        List<TypesettingDiagnostic> diagnostics, int question, ref long total)
    {
        if (segments.Count > 100) throw new ArgumentException("单题片段过多。 ", nameof(segments));
        foreach (var segment in segments)
        {
            CountText(segment.Text, ref total);
            if (!segment.IsFormula) source.Append("#text(").Append(Quote(segment.Text)).Append(") ");
            else
            {
                var formula = LatexFormulaConverter.Convert(segment.Text);
                if (formula.IsSupported)
                    source.Append("\n#fit-formula($").Append(formula.TypstMath).Append("$, ").Append(Quote(formula.Original)).Append(")\n");
                else
                {
                    diagnostics.Add(new(question, formula.Original, formula.Diagnostic!));
                    source.Append("#block(stroke: 0.4pt + rgb(\"d7a7a0\"), inset: 5pt)[#text(size: 9pt, fill: rgb(\"9c3229\"), ")
                        .Append(Quote("公式暂无法排版：" + formula.Diagnostic)).Append(") #linebreak() #text(").Append(Quote(formula.Original)).Append(")]\n");
                }
            }
        }
    }

    private const string FontTemplate = "#set text(font: \"Source Han Serif SC\", size: 11pt, lang: \"zh\")\n#show math.equation: set text(font: (\"New Computer Modern Math\", \"Source Han Serif SC\"))\n";

    private async Task CompileAsync(string sourcePath, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(sourcePath)!;
        var packages = Path.Combine(directory, "empty-packages");
        Directory.CreateDirectory(packages);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + Path.GetExtension(destination));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = directory };
        foreach (var argument in new[] { "compile", "--root", directory, "--font-path", fonts, "--ignore-system-fonts", "--package-path", packages,
            "--package-cache-path", packages, "--creation-timestamp", "0", "--jobs", "2", "--diagnostic-format", "short", sourcePath, temporary })
            start.ArgumentList.Add(argument);
        // Ignore machine-level Typst overrides; the template contains no packages or plugins.
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("TYPST_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new IOException("无法启动 Typst。 ");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var errorTask = ReadLimitedAsync(process.StandardError, timeout.Token);
        var outputTask = ReadLimitedAsync(process.StandardOutput, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var error = await errorTask;
            _ = await outputTask;
            if (process.ExitCode != 0) throw new InvalidDataException("原生排版失败：" + error);
            if (!File.Exists(temporary)) throw new IOException("Typst 未生成输出。 ");
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, true);
        }
        finally
        {
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
                await process.WaitForExitAsync(CancellationToken.None);
            }
            // Observe readers even after cancellation; bounded streams must not leave background tasks.
            try { await Task.WhenAll(errorTask, outputTask); } catch (OperationCanceledException) { }
            if (File.Exists(temporary)) File.Delete(temporary);
        }
    }

    private static async Task<string> ReadLimitedAsync(StreamReader stream, CancellationToken cancellationToken)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            if (result.Length < 65_536) result.Append(buffer, 0, Math.Min(count, 65_536 - result.Length));
        return result.ToString();
    }
}
