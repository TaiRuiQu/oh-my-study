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
        string outputDirectory, CancellationToken cancellationToken = default, ExamColumns columns = ExamColumns.Default,
        bool includeAnswerSpace = false, bool distributeAnswerSpace = false)
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
        var source = new StringBuilder();
        var labels = document.Labels ?? new ExamPrintLabels();
        source.Append(PrintFontTemplate);
        source.Append("#set document(title: ").Append(Quote(document.Title)).Append(", author: \"Oh My Study\")\n");
        source.Append(BuildHeader(labels));
        source.Append("#set page(paper: \"").Append(paper == ExamPaper.A3Landscape ? "a3" : "a4")
            .Append("\", flipped: ").Append(paper == ExamPaper.A3Landscape ? "true" : "false")
            .Append(", margin: (x: 17mm, top: 25mm, bottom: 17mm), header: exam-header, header-ascent: 5mm, footer: context align(center, text(size: 9pt)[#counter(page).display(\"1 / 1\", both: true)]))\n");
        source.Append("#set text(top-edge: 0.88em, bottom-edge: 0.32em)\n#set par(leading: 0.35em, spacing: 0pt, first-line-indent: 0pt)\n#set block(above: 0pt, below: 0pt)\n");
        source.Append(ExamLayoutTemplate);
        source.Append("#let fit-formula(pic, original) = layout(size => { if measure(pic).width > size.width { block(breakable: true)[#text(size: 9pt, \"The formula exceeds the column width. Original formula:\") #linebreak() #text(original)] } else { align(center, pic) } })\n");
        source.Append("#align(center, text(size: 18pt, weight: \"bold\", ").Append(Quote(document.Title)).Append("))\n");
        if (edition == ExamEdition.Answers)
            source.Append("#align(center, text(size: 10pt, ").Append(Quote(labels.Answers)).Append("))\n");
        source.Append("#v(4mm)\n");
        if (columnCount > 1) source.Append("#columns(").Append(columnCount).Append(", gutter: 12mm)[\n");
        long totalText = document.Title.Length;
        int nodes = 0;
        var assets = new ExportAssets(directory, edition, cancellationToken, fullWidthMillimetres, columnWidthMillimetres, columnCount);
        foreach (var question in document.Questions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (question.Number < 1) throw new ArgumentException("Question numbers must be positive integers. ", nameof(document));
            source.Append("#v(2.4mm, weak: true)\n");
            string prefix = question.Number.ToString(CultureInfo.InvariantCulture) + ". " + FormatPoints(question.Points, labels);
            bool writingSpace = edition == ExamEdition.Questions && includeAnswerSpace && question.Parts is not { Count: > 0 };
            double blank = writingSpace ? ExamAnswerSpace.HeightMillimetres(question.Answer, question.Points, question.QuestionType,
                question.Options is { Count: > 0 }, columnWidthMillimetres, question.BlankMillimetres) : 0;
            AppendSegments(source, question.Body, diagnostics, question.Number, assets, ref totalText, prefix, blank > 0);
            AppendOptions(source, question.Options, diagnostics, question.Number, assets, ref totalText);
            if (edition == ExamEdition.Answers)
            {
                AppendTextBlock(source, "Answer: " + (question.Answer ?? "Not provided"), diagnostics, question.Number, assets, ref totalText);
                if (question.Explanation is { Length: > 0 }) AppendTextBlock(source, "Explanation: " + question.Explanation, diagnostics, question.Number, assets, ref totalText);
            }
            AppendParts(source, question.Parts, 0, edition, includeAnswerSpace, labels, question.QuestionType,
                diagnostics, question.Number, assets, ref totalText, ref nodes, cancellationToken);
            if (blank > 0) AppendBlank(source, blank, assets);
        }
        if (columnCount > 1) source.Append("]\n");
        if (totalText > 1_000_000) throw new ArgumentException("Exam text exceeds the limit. ", nameof(document));
        source.Insert(0, "#let exam-extra = (:)\n");
        await File.WriteAllTextAsync(sourcePath, source.ToString(), new UTF8Encoding(false), cancellationToken);
        if (includeAnswerSpace && distributeAnswerSpace && edition == ExamEdition.Questions && assets.BlankCount > 0)
        {
            string extras = await MeasureAnswerSpaceAsync(sourcePath, columnWidthMillimetres, columnCount, cancellationToken);
            source.Replace("#let exam-extra = (:)\n", extras);
            await File.WriteAllTextAsync(sourcePath, source.ToString(), new UTF8Encoding(false), cancellationToken);
        }
        var pdf = Path.Combine(directory, name + ".pdf");
        await CompileAsync(sourcePath, pdf, cancellationToken);
        return new(pdf, sourcePath, diagnostics);
    }

    private static string BuildHeader(ExamPrintLabels labels)
    {
        var source = new StringBuilder("#let exam-header = text(size: 11pt)[#grid(columns: (auto, 1fr, auto, 1fr, auto, 1fr), column-gutter: 2mm, align: bottom,\n");
        foreach (string label in new[] { labels.Class, labels.Name, labels.StudentNumber })
            source.Append("[#text(").Append(Quote(label + ": ")).Append(")], [#box(width: 100%, height: 5mm, stroke: (bottom: 0.5pt))[]],\n");
        return source.Append(")]\n").ToString();
    }

    private static string FormatPoints(double? points, ExamPrintLabels labels)
    {
        if (points is not { } value) return "";
        if (!double.IsFinite(value) || value < 0) throw new ArgumentOutOfRangeException(nameof(points));
        return string.Format(CultureInfo.InvariantCulture, labels.Points, value) + " ";
    }

    private const string ExamLayoutTemplate = """
        #let exam-mark(kind, id: -1) = block(height: 0pt, sticky: kind != "blank")[#set text(size: 0pt, top-edge: 0pt, bottom-edge: 0pt)
          #context {
            let position = here().position()
            [#metadata((kind: kind, id: id, page: position.page, x: position.x / 1pt, y: position.y / 1pt))<exam-layout>]
          }
        ]
        #let exam-options(options) = block(breakable: true, layout(size => {
          let gap = 3mm
          let widths = options.map(option => measure(option).width)
          let tracks = (1fr,)
          if options.len() == 4 and widths.sum() + 3 * gap <= size.width {
            let extra = (size.width - 3 * gap - widths.sum()) / 4
            tracks = widths.map(width => width + extra)
          } else if options.len() >= 2 {
            let left = 0pt
            let right = 0pt
            for (index, width) in widths.enumerate() {
              if calc.rem(index, 2) == 0 { left = calc.max(left, width) }
              else { right = calc.max(right, width) }
            }
            if left + gap + right <= size.width {
              let extra = (size.width - gap - left - right) / 2
              tracks = (left + extra, right + extra)
            }
          }
          grid(columns: tracks, column-gutter: gap, row-gutter: 4pt, align: horizon, ..options.map(option => block(inset: (bottom: 2pt), option)))
        }))

        """;

    private static void AppendBlank(StringBuilder source, double millimetres, ExportAssets assets)
    {
        if (!double.IsFinite(millimetres) || millimetres < 0 || millimetres > 100) throw new ArgumentOutOfRangeException(nameof(millimetres));
        int id = assets.BlankCount++;
        source.Append("#block(height: ").Append(millimetres.ToString(CultureInfo.InvariantCulture))
            .Append("mm + exam-extra.at(\"").Append(id).Append("\", default: 0pt), breakable: false)[]\n")
            .Append("#exam-mark(\"blank\", id: ").Append(id).Append(")\n");
    }

    private static void CountText(string text, ref long total)
    {
        if (text.Length > 32_768) throw new ArgumentException("A text segment exceeds 32768 characters. ", nameof(text));
        total += text.Length;
        if (total > 1_000_000) throw new ArgumentException("Exam text exceeds the limit. ", nameof(text));
    }

    private static void AppendParts(StringBuilder source, IReadOnlyList<ExamPart>? parts, int depth,
        ExamEdition edition, bool includeAnswerSpace, ExamPrintLabels labels, string questionType,
        List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets,
        ref long total, ref int nodes, CancellationToken cancellationToken)
    {
        if (parts is null) return;
        if (depth >= 32 || parts.Count > 100) throw new ArgumentException("The depth or number of question parts exceeds the limit.");
        for (int index = 0; index < parts.Count; index++)
        {
            var part = parts[index];
            cancellationToken.ThrowIfCancellationRequested();
            if (++nodes > 10_000) throw new ArgumentException("The exam has too many question parts.");
            CountText(part.Label, ref total);
            string type = part.QuestionType.Length > 0 ? part.QuestionType : questionType;
            double blank = edition == ExamEdition.Questions && includeAnswerSpace && part.Parts is not { Count: > 0 }
                ? ExamAnswerSpace.HeightMillimetres(part.Answer, part.Points, type, part.Options is { Count: > 0 },
                    assets.ColumnWidthMillimetres, part.BlankMillimetres) : 0;
            string prefix = "（" + (index + 1).ToString(CultureInfo.InvariantCulture) + "） ";
            AppendSegments(source, part.Body, diagnostics, question, assets, ref total, prefix, blank > 0, FormatPoints(part.Points, labels).TrimEnd());
            AppendOptions(source, part.Options, diagnostics, question, assets, ref total);
            if (edition == ExamEdition.Answers)
            {
                AppendTextBlock(source, "Answer: " + (part.Answer ?? "Not provided"), diagnostics, question, assets, ref total);
                if (part.Explanation is { Length: > 0 }) AppendTextBlock(source, "Explanation: " + part.Explanation, diagnostics, question, assets, ref total);
            }
            else if (blank > 0) AppendBlank(source, blank, assets);
            AppendParts(source, part.Parts, depth + 1, edition, includeAnswerSpace, labels, type,
                diagnostics, question, assets, ref total, ref nodes, cancellationToken);
        }
    }

    private static void AppendOptions(StringBuilder source, IReadOnlyList<string>? options,
        List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets, ref long total)
    {
        if (options is not { Count: > 0 }) return;
        if (options.Count > 100) throw new ArgumentException("Too many options.");
        source.Append("#exam-mark(\"start\")\n#exam-options((\n");
        foreach (var option in options)
        {
            CountText(option, ref total);
            source.Append('[');
            AppendRichText(source, option, diagnostics, question, assets);
            source.Append("],\n");
        }
        source.Append("))\n#exam-mark(\"end\")\n");
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

    private static void AppendTextBlock(StringBuilder source, string text, List<TypesettingDiagnostic> diagnostics, int question,
        ExportAssets assets, ref long total, string? prefix = null, bool sticky = false)
    {
        CountText(text, ref total);
        source.Append("#block(breakable: true, inset: (bottom: 3.5pt), sticky: ").Append(sticky ? "true" : "false").Append(")[");
        if (prefix is not null) { source.Append("#strong["); AppendLiteral(source, prefix); source.Append(']'); }
        AppendRichText(source, text, diagnostics, question, assets);
        source.Append("]\n");
    }

    private static void AppendRichText(StringBuilder source, string text, List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets,
        double? maximumWidth = null)
    {
        var runs = DocumentText.Split(DocumentText.NormalizeMath(text));
        for (int index = 0; index < runs.Count; index++)
        {
            var run = runs[index];
            if (run.IsFormula) AppendFormula(source, run.Text, diagnostics, question, assets, run.IsDisplay, maximumWidth, run.Font);
            else
            {
                string literal = run.Text;
                if (index > 0 && runs[index - 1] is { IsFormula: true, IsDisplay: false })
                {
                    string trimmed = literal.TrimStart(' ', '\t');
                    if (trimmed.Length > 0 && IsCjk(trimmed[0])) literal = trimmed;
                }
                if (index + 1 < runs.Count && runs[index + 1] is { IsFormula: true, IsDisplay: false })
                {
                    string trimmed = literal.TrimEnd(' ', '\t');
                    if (trimmed.Length > 0 && IsCjk(trimmed[^1])) literal = trimmed;
                }
                AppendLiteral(source, literal, run.Font);
            }
        }
    }

    private static bool IsCjk(char character) => character is >= '\u3000' and <= '\u303f'
        or >= '\u3400' and <= '\u9fff' or >= '\uf900' and <= '\ufaff' or >= '\uff00' and <= '\uffef';

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
        List<TypesettingDiagnostic> diagnostics, int question, ExportAssets assets, ref long total,
        string? prefix = null, bool stickLast = false, string? suffix = null)
    {
        if (segments.Count > 1000) throw new ArgumentException("Too many segments in one question. ", nameof(segments));
        if (segments.Count == 0) AppendTextBlock(source, suffix ?? "", diagnostics, question, assets, ref total, prefix, true);
        for (int index = 0; index < segments.Count; index++)
        {
            var segment = segments[index];
            source.Append("#exam-mark(\"start\")\n");
            int kinds = (segment.Rows is null ? 0 : 1) + (segment.Image is null ? 0 : 1) + (segment.Mermaid is null ? 0 : 1);
            if (kinds > 1 || kinds == 1 && (segment.IsFormula || segment.Text.Length != 0)) throw new ArgumentException("The question segment type is ambiguous.");
            if (index == 0 && (kinds != 0 || segment.IsFormula))
            {
                AppendTextBlock(source, suffix ?? "", diagnostics, question, assets, ref total, prefix, true);
                prefix = null;
            }
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
            else AppendTextBlock(source, segment.Text + (index == 0 && suffix is { Length: > 0 } ? " " + suffix : ""), diagnostics, question, assets, ref total,
                index == 0 ? prefix : null, stickLast && index == segments.Count - 1);
            source.Append("#exam-mark(\"end\")\n");
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
                AppendRichText(source, cell.Text, diagnostics, question, assets, maximum);
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
        internal int BlankCount { get; set; }
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

    private static async Task<string> ReadLimitedAsync(StreamReader stream, CancellationToken cancellationToken, int limit = 65_536)
    {
        var result = new StringBuilder();
        var buffer = new char[2048];
        int count;
        while ((count = await stream.ReadAsync(buffer.AsMemory(), cancellationToken)) > 0)
            if (result.Length < limit) result.Append(buffer, 0, Math.Min(count, limit - result.Length));
        return result.ToString();
    }
}
