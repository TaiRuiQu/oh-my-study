using System.Diagnostics;
using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace OhMyStudy.Documents;

/// <summary>Generates only trusted templates. User text never becomes Typst source code.</summary>
public sealed partial class TypstDocumentService
{
    private readonly string executable;
    private static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var character in text)
        {
            result.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(character) => " ",
                _ => character.ToString()
            });
        }
        return result.Append('"').ToString();
    }

    public TypstDocumentService(TypstToolchain toolchain)
    {
        executable = Path.GetFullPath(toolchain.ExecutablePath);
        VerifyHash(executable, TypstToolchain.ExecutableSha256, "Typst");
    }

    private static void VerifyHash(string path, string expected, string name)
    {
        using var input = File.OpenRead(path);
        var hash = Convert.ToHexStringLower(SHA256.HashData(input));
        if (hash != expected) throw new InvalidDataException(name + " hash mismatch. Run the native document tool preparation script. ");
    }

    public async Task<ExamExportResult> ExportExamAsync(ExamDocument document, ExamPaper paper, ExamEdition edition,
        string outputDirectory, CancellationToken cancellationToken = default, ExamColumns columns = ExamColumns.Default)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (document.Questions.Count is 0 or > 500) throw new ArgumentOutOfRangeException(nameof(document), "The question count must be between 1 and 500. ");
        if (document.Title.Length > 512) throw new ArgumentException("The title is too long. ", nameof(document));
        int columnCount = columns switch
        {
            ExamColumns.Default => paper == ExamPaper.A3Landscape ? 2 : 1,
            ExamColumns.One => 1,
            ExamColumns.Two => 2,
            ExamColumns.Three => 3,
            ExamColumns.Four => 4,
            _ => throw new ArgumentOutOfRangeException(nameof(columns))
        };
        double fullWidthMillimetres = paper == ExamPaper.A3Landscape ? 386 : 176;
        double columnWidthMillimetres = (fullWidthMillimetres - 12 * (columnCount - 1)) / columnCount;
        var directory = Path.GetFullPath(outputDirectory);
        Directory.CreateDirectory(directory);
        var name = (paper == ExamPaper.A3Landscape ? "a3-" : "a4-") + (edition == ExamEdition.Questions ? "questions" : "answers");
        var sourcePath = Path.Combine(directory, name + ".typ");
        var diagnostics = new List<TypesettingDiagnostic>();
        var source = new StringBuilder("// Fixed native document template; no eval, import or user-selected paths.\n");
        source.Append(PrintFontTemplate);
        source.Append("#set document(title: ").Append(Quote(document.Title)).Append(", author: \"Oh My Study\")\n");
        source.Append("#set page(paper: \"").Append(paper == ExamPaper.A3Landscape ? "a3" : "a4")
            .Append("\", flipped: ").Append(paper == ExamPaper.A3Landscape ? "true" : "false")
            .Append(", margin: (x: 17mm, top: 18mm, bottom: 17mm), footer: context align(center, text(size: 9pt)[#counter(page).display(\"1 / 1\", both: true)]))\n");
        source.Append("#set par(leading: 0.75em, spacing: 0.65em)\n");
        source.Append("#let fit-formula(pic, original) = layout(size => { if measure(pic).width > size.width { block(breakable: true)[#text(size: 9pt, \"The formula exceeds the column width. Original formula:\") #linebreak() #text(original)] } else { align(center, pic) } })\n");
        source.Append("#align(center, text(size: 18pt, weight: \"bold\", ").Append(Quote(document.Title)).Append("))\n");
        source.Append("#align(center, text(size: 10pt, ").Append(Quote(edition == ExamEdition.Questions ? "Questions · Name: ____________  Date: ____________" : "Answers and explanations")).Append("))\n#v(4mm)\n");
        if (columnCount > 1) source.Append("#columns(").Append(columnCount).Append(", gutter: 12mm)[\n");
        long totalText = document.Title.Length;
        int nodes = 0;
        var assets = new ExportAssets(directory, edition, cancellationToken, fullWidthMillimetres, columnWidthMillimetres, columnCount);
        foreach (var question in document.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (question.Number < 1) throw new ArgumentException("Question numbers must be positive integers. ", nameof(document));
            source.Append("#block(sticky: true, above: 3mm, below: 1mm)[#strong[").Append(question.Number).Append(".]]\n");
            AppendSegments(source, question.Body, diagnostics, question.Number, assets, ref totalText);
            AppendOptions(source, question.Options, diagnostics, question.Number, assets, ref totalText);
            if (edition == ExamEdition.Answers)
            {
                // Neither StudentAttempt nor question-edition answer/explanation fields are read.
                AppendTextBlock(source, "Answer: " + (question.Answer ?? "Not provided"), diagnostics, question.Number, assets, ref totalText);
                if (question.Explanation is { Length: > 0 }) AppendTextBlock(source, "Explanation: " + question.Explanation, diagnostics, question.Number, assets, ref totalText);
            }
            AppendParts(source, question.Parts, question.Number.ToString(CultureInfo.InvariantCulture), 0,
                edition, diagnostics, question.Number, assets, ref totalText, ref nodes, cancellationToken);
            if (edition == ExamEdition.Questions) AppendBlank(source, question.BlankMillimetres);
        }
        if (columnCount > 1) source.Append("]\n");
        if (totalText > 1_000_000) throw new ArgumentException("Exam text exceeds the limit. ", nameof(document));
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
        if (text.Length > 32_768) throw new ArgumentException("A text segment exceeds 32768 characters. ", nameof(text));
        total += text.Length;
        if (total > 1_000_000) throw new ArgumentException("Exam text exceeds the limit. ", nameof(text));
    }

    private static void AppendParts(StringBuilder source, IReadOnlyList<ExamPart>? parts, string parent, int depth,
        ExamEdition edition, List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets,
        ref long total, ref int nodes, CancellationToken cancellationToken)
    {
        if (parts is null) return;
        if (depth >= 32 || parts.Count > 100) throw new ArgumentException("The depth or number of question parts exceeds the limit.");
        foreach (var part in parts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > 10_000) throw new ArgumentException("The exam has too many question parts.");
            string label = parent + " / " + part.Label;
            CountText(label, ref total);
            source.Append("#block(sticky: true, above: 2mm)[#strong[#text(").Append(Quote(label)).Append(")]]\n");
            AppendSegments(source, part.Body, diagnostics, question, assets, ref total);
            AppendOptions(source, part.Options, diagnostics, question, assets, ref total);
            if (edition == ExamEdition.Answers)
            {
                AppendTextBlock(source, "Answer: " + (part.Answer ?? "Not provided"), diagnostics, question, assets, ref total);
                if (part.Explanation is { Length: > 0 }) AppendTextBlock(source, "Explanation: " + part.Explanation, diagnostics, question, assets, ref total);
            }
            else AppendBlank(source, part.BlankMillimetres);
            AppendParts(source, part.Parts, label, depth + 1, edition, diagnostics, question, assets, ref total, ref nodes, cancellationToken);
        }
    }

    private static void AppendOptions(StringBuilder source, IReadOnlyList<string>? options,
        List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets, ref long total)
    {
        if (options is null) return;
        if (options.Count > 100) throw new ArgumentException("Too many options.");
        foreach (var option in options) AppendTextBlock(source, option, diagnostics, question, assets, ref total);
    }

    private static void AppendLiteral(StringBuilder source, string text, DocumentFont font = DocumentFont.Serif)
    {
        string family = font switch
        {
            DocumentFont.Default or DocumentFont.Serif => TypstToolchain.TextFontFamily,
            DocumentFont.SansSerif => TypstToolchain.SansFontFamily,
            DocumentFont.Kai => TypstToolchain.KaiFontFamily,
            _ => throw new ArgumentOutOfRangeException(nameof(font))
        };
        source.Append("#text(font: ").Append(Quote(family)).Append(", ").Append(Quote(DocumentText.AllowWrap(text))).Append(")");
    }

    private static void AppendTextBlock(StringBuilder source, string text, List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets, ref long total)
    {
        CountText(text, ref total);
        // Blocks can break across a page/column. Formula delimiters share the preview converter.
        source.Append("#block(breakable: true)[");
        foreach (var run in DocumentText.Split(DocumentText.NormalizeMath(text)))
        {
            if (run.IsFormula) AppendFormula(source, run.Text, diagnostics, question, assets, run.IsDisplay, font: run.Font);
            else AppendLiteral(source, run.Text, run.Font);
        }
        source.Append("]\n");
    }

    private static void AppendFormula(StringBuilder source, string text, List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets,
        bool display, double? maximumWidth = null, DocumentFont font = DocumentFont.Serif)
    {
        var formula = assets.AddFormula(text, display, out string? diagnostic);
        if (formula is not null)
        {
            if (!display && formula.WidthPoints > (maximumWidth ?? assets.ColumnWidthPoints))
            {
                diagnostics.Add(new(question, text, "The formula exceeds the column width. The original text is preserved."));
                AppendLiteral(source, text, font);
                return;
            }
            string picture = "image(" + Quote(Path.GetFileName(formula.Path)) + ", width: " + formula.WidthPoints.ToString("0.###", CultureInfo.InvariantCulture) + "pt, height: " + formula.HeightPoints.ToString("0.###", CultureInfo.InvariantCulture) + "pt)";
            if (display) source.Append("\n\n#block(width: 100%)[#fit-formula(").Append(picture).Append(", ").Append(Quote(DocumentText.AllowWrap(text))).Append(")]\n\n");
            else source.Append("#box(baseline: ").Append((formula.HeightPoints - formula.BaselinePoints).ToString("0.###", CultureInfo.InvariantCulture)).Append("pt, ").Append(picture).Append(")");
        }
        else
        {
            diagnostics.Add(new(question, text, diagnostic!));
            AppendLiteral(source, text, font);
        }
    }

    private static void AppendSegments(StringBuilder source, IReadOnlyList<ExamSegment> segments,
        List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets, ref long total)
    {
        if (segments.Count > 1000) throw new ArgumentException("Too many segments in one question. ", nameof(segments));
        foreach (var segment in segments)
        {
            int kinds = (segment.Rows is null ? 0 : 1) + (segment.Image is null ? 0 : 1) + (segment.Mermaid is null ? 0 : 1);
            if (kinds > 1 || kinds == 1 && (segment.IsFormula || segment.Text.Length != 0)) throw new ArgumentException("The question segment type is ambiguous.");
            if (segment.Rows is { } rows) AppendTable(source, rows, diagnostics, question, assets, ref total);
            else if (segment.Image is { } image)
            {
                CountText(image.Caption, ref total);
                AppendVisual(source, assets.Add(image), image.WidthPercent, image.Caption, assets, diagnostics, question, ref total);
            }
            else if (segment.Mermaid is { } mermaid)
            {
                CountText(mermaid.Code, ref total);
                CountText(mermaid.Caption, ref total);
                AppendVisual(source, assets.Add(mermaid), mermaid.WidthPercent, mermaid.Caption, assets, diagnostics, question, ref total);
            }
            else if (segment.IsFormula)
            {
                CountText(segment.Text, ref total);
                AppendFormula(source, segment.Text, diagnostics, question, assets, true);
            }
            else AppendTextBlock(source, segment.Text, diagnostics, question, assets, ref total);
        }
    }

    private static void AppendVisual(StringBuilder source, string asset, double widthPercent, string caption, ExportAssets assets,
        List<TypesettingDiagnostic> diagnostics, int question, ref long total)
    {
        double widthMillimetres = assets.FullWidthMillimetres * widthPercent / 100;
        bool spanning = assets.ColumnCount > 1 && widthMillimetres > assets.ColumnWidthMillimetres + 0.01;
        if (spanning) source.Append("#place(top + center, scope: \"parent\", float: true, clearance: 2mm)[\n");
        source.Append("#align(center, block(breakable: false)[#layout(size => { let pic = image(").Append(Quote(asset))
            .Append(", width: ").Append(widthMillimetres.ToString("0.###", CultureInfo.InvariantCulture))
            .Append("mm); if measure(pic).height > 150mm { image(").Append(Quote(asset))
            .Append(", height: 150mm) } else { pic } })])\n");
        if (caption.Length > 0) AppendTextBlock(source, caption, diagnostics, question, assets, ref total);
        if (spanning) source.Append("]\n");
    }

    private static void AppendTable(StringBuilder source, IReadOnlyList<IReadOnlyList<string>> rows,
        List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets, ref long total)
    {
        var cells = DocumentTable.Layout(rows);
        if (cells.Count == 0) return;
        int columns = rows.Max(row => row.Count);
        for (int start = 0; start < columns;)
        {
            int end = Math.Min(columns, start + 6);
            foreach (var cell in cells)
                if (cell.Column >= start && cell.Column < end && cell.Column + cell.ColumnSpan > end) end = cell.Column;
            if (end == start) throw new InvalidDataException($"A table merge beginning at column {start + 1} spans more than six columns and cannot be kept intact in a wide-table panel.");
            int width = end - start;
            bool wide = columns > 6;
            if (wide) source.Append("#block(sticky: true)[#text(size: 9pt, ")
                .Append(Quote($"Table: columns {start + 1}–{end}")).Append(")]\n");
            source.Append("#table(columns: (").Append(wide ? "auto, " : "").Append(string.Join(", ", Enumerable.Repeat("1fr", width))).Append(",), inset: 5pt, stroke: 0.4pt + rgb(\"aaaaaa\"),\n");
            if (wide)
            {
                source.Append("table.header(repeat: true, [Row], ");
                for (int column = start; column < end; column++) source.Append("[#text(").Append(Quote($"Column {column + 1}")).Append(")], ");
                source.Append("),\n");
            }
            foreach (var cell in cells.Where(cell => cell.Column >= start && cell.Column < end))
            {
                int x = cell.Column - start + (wide ? 1 : 0);
                int y = cell.Row + (wide ? 1 : 0);
                source.Append("table.cell(x: ").Append(x).Append(", y: ").Append(y).Append(", rowspan: ").Append(cell.RowSpan)
                    .Append(", colspan: ").Append(cell.ColumnSpan).Append(", breakable: true)[");
                CountText(cell.Text, ref total);
                double maximum = Math.Max(8, (assets.ColumnWidthPoints - (wide ? 30 : 0)) / width * cell.ColumnSpan - 10);
                foreach (var run in DocumentText.Split(DocumentText.NormalizeMath(cell.Text)))
                    if (run.IsFormula) AppendFormula(source, run.Text, diagnostics, question, assets, run.IsDisplay, maximum, run.Font);
                    else AppendLiteral(source, run.Text, run.Font);
                source.Append("],\n");
            }
            for (int row = 0; wide && row < rows.Count; row++)
                source.Append("table.cell(x: 0, y: ").Append(row + 1).Append(")[#text(")
                    .Append(Quote((row + 1).ToString(CultureInfo.InvariantCulture))).Append(")],\n");
            source.Append(")\n");
            start = end;
        }
    }

    private sealed class ExportAssets(string directory, ExamEdition edition, CancellationToken token,
        double fullWidthMillimetres, double columnWidthMillimetres, int columnCount)
    {
        internal double ColumnWidthPoints { get; } = columnWidthMillimetres * 72 / 25.4;
        internal double FullWidthMillimetres { get; } = fullWidthMillimetres;
        internal double ColumnWidthMillimetres { get; } = columnWidthMillimetres;
        internal int ColumnCount { get; } = columnCount;
        private readonly HashSet<string> written = new(StringComparer.Ordinal);
        private long bytes;
        private readonly Dictionary<(string, bool), (FormulaSvg? Formula, string? Diagnostic)> formulas = [];
        private long formulaBytes;
        public FormulaSvg? AddFormula(string text, bool display, out string? diagnostic)
        {
            token.ThrowIfCancellationRequested();
            if (!formulas.TryGetValue((text, display), out var cached))
            {
                if (formulas.Count >= 2048) throw new ArgumentException("The exam has too many formulas. Export smaller batches.");
                string name = "formula-" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes((display ? "display:" : "inline:") + text))) + ".svg";
                bool success = VelociTexFormulaRenderer.TryWriteSvg(text, Path.Combine(directory, name), TypstToolchain.BodySizePoints, display, token, out var formula, out diagnostic);
                if (success)
                {
                    formulaBytes += new FileInfo(formula!.Path).Length;
                    if (formulaBytes > 128L * 1024 * 1024) throw new ArgumentException("The total formula content exceeds the limit.");
                }
                cached = (formula, diagnostic);
                formulas.Add((text, display), cached);
            }
            diagnostic = cached.Diagnostic;
            return cached.Formula;
        }

        public string Add(ExamImage image)
        {
            if (image.ContainsHandwriting && edition == ExamEdition.Questions)
                throw new InvalidDataException("Some question images contain handwriting. Replace them with clean images before exporting a question paper.");
            if (image.Bytes.Length is < 8 or > 64 * 1024 * 1024) throw new InvalidDataException("The question image is empty or larger than 64 MB.");
            string extension = image.Bytes.AsSpan().StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }) ? ".png"
                : image.Bytes[0] == 255 && image.Bytes[1] == 216 && image.Bytes[2] == 255 ? ".jpg"
                : throw new InvalidDataException("Question images must be decoded PNG or JPEG images.");
            ValidateRasterDimensions(image.Bytes, extension);
            string name = "asset-" + Convert.ToHexStringLower(SHA256.HashData(image.Bytes)) + extension;
            if (written.Add(name))
            {
                bytes += image.Bytes.LongLength;
                if (written.Count > 512 || bytes > 128L * 1024 * 1024) throw new ArgumentException("Total question image size exceeds the limit.");
                File.WriteAllBytes(Path.Combine(directory, name), image.Bytes);
            }
            return name;
        }

        public string Add(ExamMermaid mermaid)
        {
            token.ThrowIfCancellationRequested();
            string svg = MermaidRenderer.RenderSvg(mermaid.Code);
            byte[] content = Encoding.UTF8.GetBytes(svg);
            string name = "mermaid-" + Convert.ToHexStringLower(SHA256.HashData(content)) + ".svg";
            if (written.Add(name))
            {
                bytes += content.LongLength;
                if (written.Count > 512 || bytes > 128L * 1024 * 1024) throw new ArgumentException("Total question image size exceeds the limit.");
                File.WriteAllBytes(Path.Combine(directory, name), content);
            }
            token.ThrowIfCancellationRequested();
            return name;
        }

        private static void ValidateRasterDimensions(byte[] bytes, string extension)
        {
            uint width = 0, height = 0;
            if (extension == ".png")
            {
                if (bytes.Length < 33 || !bytes.AsSpan(12, 4).SequenceEqual("IHDR"u8)) throw new InvalidDataException("The question image file is incomplete.");
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
                throw new InvalidDataException("Question image dimensions are invalid or exceed the limit.");
        }
    }

    /// <summary>Only print prose uses the installed Windows 新宋体; formula outlines come from VelociTex.</summary>
    private static readonly string PrintFontTemplate = "#set text(font: " + Quote(TypstToolchain.TextFontFamily)
        + ", size: " + TypstToolchain.BodySizePoints.ToString(CultureInfo.InvariantCulture) + "pt, lang: \"zh\")\n";

    private async Task CompileAsync(string sourcePath, string destination, CancellationToken cancellationToken, int pages = 0)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var directory = Path.GetDirectoryName(sourcePath)!;
        var packages = Path.Combine(directory, "empty-packages");
        Directory.CreateDirectory(packages);
        var temporary = Path.Combine(directory, Guid.NewGuid().ToString("N") + (pages > 0 ? "-{p}" : "") + Path.GetExtension(destination));
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, RedirectStandardOutput = true, WorkingDirectory = directory };
        foreach (var argument in new[] { "compile", "--root", directory, "--package-path", packages,
            "--package-cache-path", packages, "--creation-timestamp", "0", "--jobs", "2", "--diagnostic-format", "short", sourcePath, temporary })
            start.ArgumentList.Add(argument);
        // Ignore machine-level Typst overrides; the template contains no packages or plugins.
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("TYPST_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        using var process = Process.Start(start) ?? throw new IOException("Cannot start Typst. ");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        var errorTask = ReadLimitedAsync(process.StandardError, timeout.Token);
        var outputTask = ReadLimitedAsync(process.StandardOutput, timeout.Token);
        try
        {
            await process.WaitForExitAsync(timeout.Token);
            var error = await errorTask;
            _ = await outputTask;
            if (process.ExitCode != 0) throw new InvalidDataException("Native typesetting failed: " + error);
            // A family that is not installed is only a warning, and the engine would silently
            // substitute another face. Fail loudly instead, so a PDF never claims a font it lacks.
            int missing = error.IndexOf("unknown font family", StringComparison.OrdinalIgnoreCase);
            if (missing >= 0)
            {
                string family = error[missing..].Split('\n')[0]["unknown font family".Length..].TrimStart(' ', ':', '：');
                throw new InvalidDataException($"The required font '{family}' is missing. Install the required Windows font and retry.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            if (pages == 0)
            {
                if (!File.Exists(temporary)) throw new IOException("Typst did not generate output. ");
                File.Move(temporary, destination, true);
            }
            else
            {
                for (int page = 1; page <= pages; page++)
                {
                    string number = page.ToString(CultureInfo.InvariantCulture);
                    File.Move(temporary.Replace("{p}", number), destination.Replace("{p}", number), true);
                }
            }
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
            for (int page = 1; page <= pages; page++)
            {
                string output = temporary.Replace("{p}", page.ToString(CultureInfo.InvariantCulture));
                if (File.Exists(output)) File.Delete(output);
            }
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
