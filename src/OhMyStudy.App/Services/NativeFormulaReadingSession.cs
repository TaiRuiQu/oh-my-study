using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Documents;
using VelociTex;
using VelociTex.Rendering.Direct2D;
using VelociTex.WinUI;

namespace OhMyStudy.App.Services;

/// <summary>Single-formula compilation and host-owned paragraph composition. No image display intermediate.</summary>
internal sealed class NativeFormulaReadingSession(double width, bool preview, ElementTheme initialTheme)
{
    private static readonly MathEngine Engine = new();
    private static readonly WindowsMathTextShaper TextShaper = new();
    private sealed record Compilation(MathDocument? Document, MathDiagnostic Diagnostic);
    private static readonly MathDocumentCache Formulas = new(16 * 1024 * 1024, 512);
    private static readonly BoundedPageCache<(string, double), double> PreviewHeights = new(512 * 1024, 512);
    private static readonly SemaphoreSlim CompilerSlot = new(1, 1);
    private static int _pending;
    private readonly double _width = Math.Clamp(width, 40, 4000);
    private readonly bool _preview = preview;
    private readonly ElementTheme _theme = initialTheme;
    internal double Width => _width;
    internal static long Compilations;
    internal static long CacheHits;
    internal static long InvalidFormulas;
    internal static int CachedFormulaCount => Formulas.Count;
    internal static long CachedFormulaBytes => Formulas.EstimatedRetainedBytes;
    internal static int PendingFormulaCount => Volatile.Read(ref _pending);
    internal static void ClearCaches() { Formulas.Clear(); PreviewHeights.Clear(); TextShaper.Clear(); }

    internal static string ReadingFontFamily(DocumentFont font) => font switch
    {
        DocumentFont.Serif => "NSimSun",
        DocumentFont.SansSerif => "Microsoft YaHei",
        DocumentFont.Kai => "KaiTi",
        _ => UiText.ReadingFontFamily
    };

    internal static double? CachedPreviewHeight(string source, double width)
        => PreviewHeights.TryGet((source, Math.Floor(width)), out double result) ? result : null;

    internal static async Task PrepareAsync(IEnumerable<string> texts, bool preview, CancellationToken token)
    {
        foreach (string text in texts.Distinct(StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            foreach (var run in DocumentText.Split(DocumentText.NormalizeMath(text)))
                if (run.IsFormula) await CompileAsync(run.Text, run.IsDisplay && !preview, preview ? 14 : run.IsDisplay ? 20 : 16, token);
        }
    }

    internal async Task<FrameworkElement> PresentAsync(string source, CancellationToken token, bool table, ElementTheme? requestedTheme, double? width = null)
    {
        token.ThrowIfCancellationRequested();
        double available = Math.Clamp(width ?? _width, 40, 4000);
        string normalized = DocumentText.NormalizeMath(source);
        var runs = DocumentText.Split(normalized);
        var blocks = new List<FrameworkElement>(); var parts = new List<NativeFormulaParagraph.Part>(); var paragraphSource = new StringBuilder(); double previewHeight = 20;
        foreach (var run in runs)
        {
            token.ThrowIfCancellationRequested();
            if (run.IsDisplay && !_preview)
            {
                FlushParagraph();
                var result = await CompileAsync(run.Text, true, 20, token);
                if (result.Document is null) blocks.Add(Plain("$$" + run.Text + "$$", run.Font));
                else
                {
                    if (FormulaWidth(result.Document) + 4 > available)
                    {
                        var wrapped = await FitFormulaAsync(result.Document, true, 20, available, token);
                        blocks.Add(new NativeFormulaParagraph(source, wrapped, available, 20, false));
                        continue;
                    }
                    MathView formula = new() { Result = result.Document, AccessibleDescription = run.Text };
                    // MathView measures fractional DIPs but allocates whole pixels. Matching
                    // the surface extent avoids resampling its glyphs by a fractional ratio.
                    XamlRoot? observedRoot = null;
                    void AlignSurface(XamlRoot? root, XamlRootChangedEventArgs? args)
                    {
                        double scale = root?.RasterizationScale ?? 1;
                        double pixelWidth = Math.Ceiling(formula.Width * scale) / scale;
                        double pixelHeight = Math.Ceiling(formula.Height * scale) / scale;
                        if (Math.Abs(pixelWidth - formula.Width) < .0001 && Math.Abs(pixelHeight - formula.Height) < .0001) return;
                        formula.Width = pixelWidth; formula.Height = pixelHeight;
                        formula.Rebuild();
                    }
                    formula.Loaded += (_, _) =>
                    {
                        observedRoot = formula.XamlRoot;
                        if (observedRoot is not null) observedRoot.Changed += AlignSurface;
                        AlignSurface(observedRoot, null);
                    };
                    formula.Unloaded += (_, _) =>
                    {
                        if (observedRoot is not null) observedRoot.Changed -= AlignSurface;
                        observedRoot = null;
                    };
                    formula.HorizontalAlignment = HorizontalAlignment.Left;
                    blocks.Add(formula);
                }
                continue;
            }
            if (run.IsFormula)
            {
                var result = await CompileAsync(run.Text, false, _preview ? 14 : 16, token);
                if (_preview && result.Document is null)
                {
                    PreviewHeights.Set((source, Math.Floor(_width)), 20, source.Length * 2L + 64);
                    return Plain(UiText.Get("Preview unavailable"));
                }
                if (_preview && result.Document is { } large && (Math.Max(large.LogicalSize.Height, large.InkBounds.Y + large.InkBounds.Height) - Math.Min(0, large.InkBounds.Y) + 2 > 46
                    || Math.Max(large.LogicalSize.Width, large.InkBounds.X + large.InkBounds.Width) - Math.Min(0, large.InkBounds.X) + 2 > available))
                {
                    paragraphSource.Append('…'); parts.Add(new("…", null, null, run.Font));
                    break;
                }
                string delimiter = run.IsDisplay ? "$$" : "$";
                string marked = delimiter + run.Text + delimiter;
                paragraphSource.Append(marked);
                if (!_preview && result.Document is { } document && FormulaWidth(document) + 4 > available)
                    parts.AddRange(await FitFormulaAsync(document, false, 16, table ? Math.Min(available, 280) : available, token));
                else parts.Add(new(result.Document is null ? marked : string.Empty, result.Document, null, run.Font));
            }
            else
            {
                // Keep individual surface sizes bounded. Newlines are paragraph boundaries, not TeX line breaking.
                string[] lines = run.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0 && !_preview) FlushParagraph();
                    parts.Add(new(lines[i], null, null, run.Font)); paragraphSource.Append(lines[i]);
                    if (i + 1 < lines.Length && _preview) { parts.Add(new(" ", null, null, run.Font)); paragraphSource.Append(' '); }
                }
            }
        }
        FlushParagraph();
        if (blocks.Count == 0) blocks.Add(Plain(runs.Count == 0 ? "" : source));
        FrameworkElement host = blocks.Count == 1 ? blocks[0] : new StackPanel { Spacing = 6 };
        if (host is StackPanel panel) foreach (var block in blocks) panel.Children.Add(block);
        host.RequestedTheme = requestedTheme ?? _theme; host.IsHitTestVisible = !_preview;
        host.Loaded += (_, _) => host.RequestedTheme = ElementTheme.Default;
        if (_preview)
        {
            double height = Math.Clamp(previewHeight, 20, 48);
            PreviewHeights.Set((source, Math.Floor(_width)), height, source.Length * 2L + 64);
        }
        return host;

        void FlushParagraph()
        {
            if (parts.Count == 0) return;
            string original = paragraphSource.ToString();
            if (parts.Any(part => part.Formula is not null))
            {
                try
                {
                    NativeFormulaParagraph paragraph = new(source, parts.ToArray(), table ? Math.Min(available, 280) : available, _preview ? 14 : 16, _preview);
                    previewHeight = paragraph.Height;
                    blocks.Add(paragraph);
                }
                catch (InvalidOperationException) { blocks.Add(Plain(original)); }
            }
            else
            {
                TextBlock literal = Literal(parts);
                literal.Measure(new Windows.Foundation.Size(available, 10000)); previewHeight = literal.DesiredSize.Height;
                blocks.Add(literal);
            }
            parts.Clear(); paragraphSource.Clear();
        }
        TextBlock Literal(IReadOnlyList<NativeFormulaParagraph.Part> sourceParts)
        {
            TextBlock literal = Plain("");
            literal.Inlines.Clear();
            foreach (var part in sourceParts)
                literal.Inlines.Add(new Run { Text = part.Text, FontFamily = new FontFamily(ReadingFontFamily(part.Font)) });
            return literal;
        }
        TextBlock Plain(string text, DocumentFont font = DocumentFont.Default) => new() { Text = text, FontFamily = new FontFamily(ReadingFontFamily(font)), FontSize = _preview ? 14 : 16, TextWrapping = TextWrapping.Wrap, MaxLines = _preview ? 2 : 0, TextTrimming = _preview ? TextTrimming.CharacterEllipsis : TextTrimming.None, IsTextSelectionEnabled = !_preview, HorizontalAlignment = HorizontalAlignment.Stretch };
    }

    private static double FormulaWidth(MathDocument document) =>
        Math.Max(document.LogicalSize.Width, document.InkBounds.X + document.InkBounds.Width) - Math.Min(0, document.InkBounds.X);

    private static async Task<IReadOnlyList<NativeFormulaParagraph.Part>> FitFormulaAsync(MathDocument document, bool display, double fontSize, double width, CancellationToken token)
    {
        var segments = FormulaLineBreaks.Split(document.Source);
        var parts = new List<NativeFormulaParagraph.Part>();
        foreach (string segment in segments)
        {
            var result = segments.Count == 1 ? new Compilation(document, default) : await CompileAsync("{}" + segment, display, fontSize, token);
            if (result.Document is not { } fragment)
            {
                parts.Clear();
                break;
            }
            double maximum = Math.Max(1, width - 6);
            if (FormulaWidth(fragment) > maximum)
            {
                result = await CompileAsync(fragment.Source, display, fontSize * maximum / FormulaWidth(fragment), token);
                if (result.Document is not { } fitted) { parts.Clear(); break; }
                fragment = fitted;
            }
            parts.Add(new("", fragment, document.Source));
        }
        if (parts.Count != 0) return parts;
        var fallback = await CompileAsync(document.Source, display, fontSize * Math.Max(1, width - 6) / FormulaWidth(document), token);
        return (NativeFormulaParagraph.Part[])[new("", fallback.Document ?? document, document.Source)];
    }

    private static async Task<Compilation> CompileAsync(string source, bool display, double fontSize, CancellationToken token)
    {
        if (Interlocked.Increment(ref _pending) > 64) { Interlocked.Decrement(ref _pending); return new(null, new(MathDiagnosticCode.NodeLimit, new(0, source.Length), "Too many pending formulas.")); }
        try
        {
            await CompilerSlot.WaitAsync(token);
            try
            {
                return await Task.Run(() =>
                {
                    using MathWorkspace workspace = new(); var options = new MathOptions { FontSize = fontSize, DisplayMode = display };
                    bool success = Formulas.TryCompile(Engine, source, in options, workspace, token, out var document, out var diagnostic, out bool hit);
                    if (hit) Interlocked.Increment(ref CacheHits); else Interlocked.Increment(ref Compilations);
                    if (!success && diagnostic.Code == MathDiagnosticCode.UnknownSymbol)
                    {
                        // The public cache deliberately bypasses a mutable external text provider.
                        // CJK glyph outlines remain independently bounded inside the provider.
                        options = options with { TextShaper = TextShaper };
                        success = Engine.TryCompile(source, in options, workspace, token, out document, out diagnostic);
                        Interlocked.Increment(ref Compilations);
                    }
                    if (!success)
                    {
                        Interlocked.Increment(ref InvalidFormulas);
                        AppLog.Failure("Question.NativeFormula", $"Compilation failed: {diagnostic}; source: {source}");
                    }
                    if (diagnostic.Code == MathDiagnosticCode.Cancelled) throw new OperationCanceledException(token);
                    return new Compilation(document, diagnostic);
                }, token);
            }
            finally { CompilerSlot.Release(); }
        }
        finally { Interlocked.Decrement(ref _pending); }
    }

}
