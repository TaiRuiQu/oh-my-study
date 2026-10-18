using System.Diagnostics;
using System.Buffers.Binary;
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
        if (latex.Length > 32_768) throw new ArgumentException("公式原文超过 32768 字符。", nameof(latex));
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var conversion = LatexFormulaConverter.Convert(latex);
        var sourcePath = Path.Combine(directory, "formula.typ");
        var source = "#set page(width: auto, height: auto, margin: 3pt, fill: none)\n" + FontTemplate
            + (conversion.IsSupported ? "#context { let eq = $ " + conversion.TypstMath + " $\n if measure(eq).width > 560pt {\nblock(width: 560pt)[#text(size: 9pt, \"公式原文：\") #linebreak() #text(" + Quote(DocumentText.AllowWrap(latex)) + ")]\n} else { eq } }"
                : "#block(width: 320pt)[#text(size: 9pt, \"公式原文：\") #linebreak() #text(" + Quote(DocumentText.AllowWrap(latex)) + ")]\n");
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
        int nodes = 0;
        var assets = new ExportAssets(directory, edition);
        foreach (var question in document.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (question.Number < 1) throw new ArgumentException("题号必须为正整数。 ", nameof(document));
            source.Append("#block(sticky: true, above: 3mm, below: 1mm)[#strong[").Append(question.Number).Append(".]]\n");
            AppendSegments(source, question.Body, diagnostics, question.Number, assets, ref totalText);
            AppendOptions(source, question.Options, diagnostics, question.Number, ref totalText);
            if (edition == ExamEdition.Answers)
            {
                // Neither StudentAttempt nor question-edition answer/explanation fields are read.
                AppendTextBlock(source, "答案：" + (question.Answer ?? "未提供"), diagnostics, question.Number, ref totalText);
                if (question.Explanation is { Length: > 0 }) AppendTextBlock(source, "解析：" + question.Explanation, diagnostics, question.Number, ref totalText);
            }
            AppendParts(source, question.Parts, question.Number.ToString(CultureInfo.InvariantCulture), 0,
                edition, diagnostics, question.Number, assets, ref totalText, ref nodes, cancellationToken);
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

    private static void AppendParts(StringBuilder source, IReadOnlyList<ExamPart>? parts, string parent, int depth,
        ExamEdition edition, List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets,
        ref long total, ref int nodes, CancellationToken cancellationToken)
    {
        if (parts is null) return;
        if (depth >= 32 || parts.Count > 100) throw new ArgumentException("小问层级或数量超过限制。");
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > 10_000) throw new ArgumentException("试卷小问过多。");
            string label = parent + " / " + part.Label;
            CountText(label, ref total);
            source.Append("#block(sticky: true, above: 2mm)[#strong[#text(").Append(Quote(label)).Append(")]]\n");
            AppendSegments(source, part.Body, diagnostics, question, assets, ref total);
            AppendOptions(source, part.Options, diagnostics, question, ref total);
            if (edition == ExamEdition.Answers)
            {
                AppendTextBlock(source, "答案：" + (part.Answer ?? "未提供"), diagnostics, question, ref total);
                if (part.Explanation is { Length: > 0 }) AppendTextBlock(source, "解析：" + part.Explanation, diagnostics, question, ref total);
            }
            else AppendBlank(source, part.BlankMillimetres);
            AppendParts(source, part.Parts, label, depth + 1, edition, diagnostics, question, assets, ref total, ref nodes, cancellationToken);
        }
    }

    private static void AppendOptions(StringBuilder source, IReadOnlyList<string>? options,
        List<TypesettingDiagnostic> diagnostics, int question, ref long total)
    {
        if (options is null) return;
        if (options.Count > 100) throw new ArgumentException("选项数过多。");
        foreach (var option in options) AppendTextBlock(source, option, diagnostics, question, ref total);
    }

    private static void AppendTextBlock(StringBuilder source, string text, List<TypesettingDiagnostic> diagnostics, int question, ref long total)
    {
        CountText(text, ref total);
        // Blocks can break across a page/column. Formula delimiters share the preview converter.
        source.Append("#block(breakable: true)[");
        foreach (var run in DocumentText.Split(text))
        {
            if (run.IsFormula) AppendFormula(source, run.Text, diagnostics, question);
            else source.Append("#text(").Append(Quote(DocumentText.AllowWrap(run.Text))).Append(")");
        }
        source.Append("]\n");
    }

    private static void AppendFormula(StringBuilder source, string text, List<TypesettingDiagnostic> diagnostics, int question)
    {
        var formula = LatexFormulaConverter.Convert(text);
        if (formula.IsSupported)
            source.Append("\n#fit-formula($").Append(formula.TypstMath).Append("$, ").Append(Quote(DocumentText.AllowWrap(text))).Append(")\n");
        else
        {
            diagnostics.Add(new(question, text, formula.Diagnostic!));
            source.Append("#block(breakable: true, inset: 4pt)[#text(size: 9pt, ")
                .Append(Quote("公式原文：")).Append(") #linebreak() #text(").Append(Quote(DocumentText.AllowWrap(text))).Append(")]\n");
        }
    }

    private static void AppendSegments(StringBuilder source, IReadOnlyList<ExamSegment> segments,
        List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets, ref long total)
    {
        if (segments.Count > 1000) throw new ArgumentException("单题片段过多。 ", nameof(segments));
        foreach (var segment in segments)
        {
            if ((segment.Rows is not null && segment.Image is not null) || ((segment.Rows is not null || segment.Image is not null) && (segment.IsFormula || segment.Text.Length != 0)))
                throw new ArgumentException("题目片段类型不明确。");
            if (segment.Rows is { } rows) AppendTable(source, rows, diagnostics, question, ref total);
            else if (segment.Image is { } image)
            {
                CountText(image.Caption, ref total);
                string asset = assets.Add(image);
                // Explicit height cap avoids an unbreakable image overflowing a page or column.
                source.Append("#block(breakable: false)[#layout(size => { let pic = image(").Append(Quote(asset))
                    .Append(", width: size.width); if measure(pic).height > 150mm { align(center, image(").Append(Quote(asset))
                    .Append(", height: 150mm)) } else { pic } })]\n");
                if (image.Caption.Length > 0) AppendTextBlock(source, image.Caption, diagnostics, question, ref total);
            }
            else if (segment.IsFormula)
            {
                CountText(segment.Text, ref total);
                AppendFormula(source, segment.Text, diagnostics, question);
            }
            else AppendTextBlock(source, segment.Text, diagnostics, question, ref total);
        }
    }

    private static void AppendTable(StringBuilder source, IReadOnlyList<IReadOnlyList<string>> rows,
        List<TypesettingDiagnostic> diagnostics, int question, ref long total)
    {
        if (rows.Count == 0) return;
        int columns = rows.Max(row => row.Count);
        if (columns == 0) return;
        if (rows.Count > 2000 || columns > 100 || (long)rows.Count * columns > 50_000) throw new ArgumentException("表格超过限制。");
        // Split wide tables into column panels with explicit original column numbers, preserving every cell.
        for (int start = 0; start < columns; start += 6)
        {
            int width = Math.Min(6, columns - start);
            bool wide = columns > 6;
            if (columns > 6) source.Append("#block(sticky: true)[#text(size: 9pt, ")
                .Append(Quote($"表格：第 {start + 1}–{start + width} 列")).Append(")]\n");
            source.Append("#table(columns: (").Append(wide ? "auto, " : "").Append(string.Join(", ", Enumerable.Repeat("1fr", width))).Append(",), inset: 5pt, stroke: 0.4pt + rgb(\"aaaaaa\"),\n");
            if (wide)
            {
                source.Append("table.header(repeat: true, [行], ");
                for (int column = start; column < start + width; column++) source.Append("[#text(").Append(Quote($"第 {column + 1} 列")).Append(")], ");
                source.Append("),\n");
            }
            for (int rowIndex = 0; rowIndex < rows.Count; rowIndex++)
            {
                var row = rows[rowIndex];
                if (wide) source.Append("[#text(").Append(Quote((rowIndex + 1).ToString(CultureInfo.InvariantCulture))).Append(")],\n");
                for (int column = start; column < start + width; column++)
                {
                    source.Append("table.cell(breakable: true)[");
                    string cell = column < row.Count ? row[column] : "";
                    CountText(cell, ref total);
                    foreach (var run in DocumentText.Split(cell))
                        if (run.IsFormula) AppendFormula(source, run.Text, diagnostics, question);
                        else source.Append("#text(").Append(Quote(DocumentText.AllowWrap(run.Text))).Append(")");
                    source.Append("],\n");
                }
            }
            source.Append(")\n");
        }
    }

    private sealed class ExportAssets(string directory, ExamEdition edition)
    {
        private readonly HashSet<string> written = new(StringComparer.Ordinal);
        private long bytes;
        public string Add(ExamImage image)
        {
            if (image.ContainsHandwriting && edition == ExamEdition.Questions)
                throw new InvalidDataException("题图中含有手写内容，请更换干净题图后导出题目卷。");
            if (image.Bytes.Length is < 8 or > 64 * 1024 * 1024) throw new InvalidDataException("题图为空或超过 64 MB。");
            string extension = image.Bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? ".png"
                : image.Bytes[0] == 255 && image.Bytes[1] == 216 && image.Bytes[2] == 255 ? ".jpg"
                : throw new InvalidDataException("题图必须为已解码的 PNG 或 JPEG 图片。");
            ValidateRasterDimensions(image.Bytes, extension);
            string name = "asset-" + Convert.ToHexStringLower(SHA256.HashData(image.Bytes)) + extension;
            if (written.Add(name))
            {
                bytes += image.Bytes.LongLength;
                if (written.Count > 512 || bytes > 128L * 1024 * 1024) throw new ArgumentException("题图总量超过限制。");
                File.WriteAllBytes(Path.Combine(directory, name), image.Bytes);
            }
            return name;
        }

        private static void ValidateRasterDimensions(byte[] bytes, string extension)
        {
            uint width = 0, height = 0;
            if (extension == ".png")
            {
                if (bytes.Length < 33 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("题图文件不完整。");
                width = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
                height = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
            }
            else
            {
                int offset = 2;
                while (offset < bytes.Length)
                {
                    if (bytes[offset++] != 255) break;
                    while (offset < bytes.Length && bytes[offset] == 255) offset++;
                    if (offset >= bytes.Length) break;
                    byte marker = bytes[offset++];
                    if (marker is 0xd9 or 0xda) break;
                    if (marker is 0x01 or >= 0xd0 and <= 0xd8) continue;
                    if (offset + 2 > bytes.Length) break;
                    int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));
                    if (length < 2 || offset + length > bytes.Length) break;
                    if (marker is >= 0xc0 and <= 0xcf && marker is not (0xc4 or 0xc8 or 0xcc))
                    {
                        if (length < 8) break;
                        height = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 3, 2));
                        width = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset + 5, 2));
                        break;
                    }
                    offset += length;
                }
            }
            if (width is 0 or > 20000 || height is 0 or > 20000 || (ulong)width * height > 40_000_000)
                throw new InvalidDataException("题图尺寸无效或超过限制。");
        }
    }

    private static readonly string FontTemplate = "#set text(font: " + Quote(TypstToolchain.TextFontFamily)
        + ", size: " + TypstToolchain.BodySizePoints.ToString(CultureInfo.InvariantCulture) + "pt, lang: \"zh\")\n#show math.equation: set text(font: ("
        + Quote(TypstToolchain.MathFontFamily) + ", " + Quote(TypstToolchain.TextFontFamily) + "))\n";

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
