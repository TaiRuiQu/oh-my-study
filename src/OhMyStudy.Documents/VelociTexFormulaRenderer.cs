using System.Globalization;
using System.Xml.Linq;
using VelociTex;
using VelociTex.Rendering.Direct2D;

namespace OhMyStudy.Documents;

public static class DocumentTypography
{
    public const string InterfaceFontFamily = "Microsoft YaHei UI";
    public const double InterfaceBodySizePoints = 12;
}

public sealed record FormulaSvg(string Path, double WidthPoints, double HeightPoints, double BaselinePoints);

/// <summary>Compiles original TeX directly; no TeX-to-Typst conversion or native resource crosses a thread.</summary>
public static class VelociTexFormulaRenderer
{
    public static bool TryWriteSvg(string source, string path, double fontSizePoints, bool display,
        CancellationToken token, out FormulaSvg? result, out string? diagnostic)
    {
        token.ThrowIfCancellationRequested();
        var runs = DocumentText.Split(source.Trim());
        string tex = DocumentText.NormalizeFormula(runs.Count == 1 && runs[0].IsFormula ? runs[0].Text : source);
        using var workspace = new MathWorkspace();
        var options = new MathOptions { FontSize = fontSizePoints * 96 / 72, DisplayMode = display, TextShaper = new WindowsMathTextShaper() };
        if (!new MathEngine().TryCompile(tex, in options, workspace, token, out var document, out var error))
        {
            if (error.Code == MathDiagnosticCode.Cancelled) throw new OperationCanceledException(token);
            result = null;
            diagnostic = error.Message;
            return false;
        }
        token.ThrowIfCancellationRequested();
        double width = Math.Max(document!.LogicalSize.Width, document.InkBounds.X + document.InkBounds.Width) - Math.Min(0, document.InkBounds.X) + 2;
        double height = Math.Max(document.LogicalSize.Height, document.InkBounds.Y + document.InkBounds.Height) - Math.Min(0, document.InkBounds.Y) + 2;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width > 32768 || height > 32768)
        {
            result = null;
            diagnostic = "The formula is too large. The original text is preserved.";
            return false;
        }
        using var renderer = new WindowsMathRenderer();
        renderer.WriteSvg(document!, path, padding: 1);
        var svg = XElement.Load(path);
        double Dimension(string name) => double.Parse(svg.Attribute(name)!.Value, CultureInfo.InvariantCulture) * 72 / 96;
        double top = Math.Min(0, document!.InkBounds.Y);
        result = new(path, Dimension("width"), Dimension("height"), (document.Baseline - top + 1) * 72 / 96);
        diagnostic = null;
        return true;
    }
}
