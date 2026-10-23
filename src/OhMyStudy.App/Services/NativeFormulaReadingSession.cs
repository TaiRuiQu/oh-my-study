using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Controls;
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
    internal static long Compilations;
    internal static long CacheHits;
    internal static long InvalidFormulas;
    internal static int CachedFormulaCount => Formulas.Count;
    internal static long CachedFormulaBytes => Formulas.EstimatedRetainedBytes;
    internal static int PendingFormulaCount => Volatile.Read(ref _pending);
    internal static void ClearCaches() { Formulas.Clear(); PreviewHeights.Clear(); TextShaper.Clear(); }

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

    internal async Task<FrameworkElement> PresentAsync(string source, CancellationToken token, bool table, ElementTheme? requestedTheme)
    {
        token.ThrowIfCancellationRequested();
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
                if (result.Document is null) blocks.Add(Plain("$$" + run.Text + "$$"));
                else
                {
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
                    blocks.Add(new ScrollViewer { Content = formula, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalAlignment = HorizontalAlignment.Stretch });
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
                    || Math.Max(large.LogicalSize.Width, large.InkBounds.X + large.InkBounds.Width) - Math.Min(0, large.InkBounds.X) + 2 > _width))
                {
                    paragraphSource.Append('…'); parts.Add(new("…", null));
                    break;
                }
                string delimiter = run.IsDisplay ? "$$" : "$";
                string marked = delimiter + run.Text + delimiter;
                paragraphSource.Append(marked); parts.Add(new(result.Document is null ? marked : string.Empty, result.Document));
            }
            else
            {
                // Keep individual surface sizes bounded. Newlines are paragraph boundaries, not TeX line breaking.
                string[] lines = run.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    if (i > 0 && !_preview) FlushParagraph();
                    parts.Add(new(lines[i], null)); paragraphSource.Append(lines[i]);
                    if (i + 1 < lines.Length && _preview) { parts.Add(new(" ", null)); paragraphSource.Append(' '); }
                }
            }
        }
        FlushParagraph();
        if (blocks.Count == 0) blocks.Add(Plain(source));
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
                    NativeFormulaParagraph paragraph = new(source, parts.ToArray(), table ? Math.Min(_width, 280) : _width, _preview ? 14 : 16, _preview);
                    previewHeight = paragraph.Height;
                    blocks.Add(new ScrollViewer { Content = paragraph, HorizontalScrollBarVisibility = _preview ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = _preview ? ScrollMode.Disabled : ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled, IsTabStop = false });
                }
                catch (InvalidOperationException) { blocks.Add(Plain(original)); }
            }
            else
            {
                TextBlock literal = Plain(string.Concat(parts.Select(part => part.Text)));
                literal.Measure(new Windows.Foundation.Size(_width, 10000)); previewHeight = literal.DesiredSize.Height;
                blocks.Add(literal);
            }
            parts.Clear(); paragraphSource.Clear();
        }
        TextBlock Plain(string text) => new() { Text = text, FontFamily = new FontFamily(UiText.ReadingFontFamily), FontSize = _preview ? 14 : 16, TextWrapping = TextWrapping.Wrap, MaxLines = _preview ? 2 : 0, TextTrimming = _preview ? TextTrimming.CharacterEllipsis : TextTrimming.None, IsTextSelectionEnabled = !_preview, HorizontalAlignment = HorizontalAlignment.Stretch };
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
                    if (!success) Interlocked.Increment(ref InvalidFormulas);
                    if (diagnostic.Code == MathDiagnosticCode.Cancelled) throw new OperationCanceledException(token);
                    return new Compilation(document, diagnostic);
                }, token);
            }
            finally { CompilerSlot.Release(); }
        }
        finally { Interlocked.Decrement(ref _pending); }
    }

}
