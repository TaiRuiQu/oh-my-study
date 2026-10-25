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
        double scale = document.Rules.IsEmpty ? 1 : Math.Min(32, Math.Min(1024 / options.FontSize, 32768 / Math.Max(width, height)));
        if (scale > 1)
        {
            options = options with { FontSize = options.FontSize * scale };
            if (!new MathEngine().TryCompile(tex, in options, workspace, token, out document, out error))
            {
                if (error.Code == MathDiagnosticCode.Cancelled) throw new OperationCanceledException(token);
                result = null;
                diagnostic = error.Message;
                return false;
            }
        }
        using var renderer = new WindowsMathRenderer();
        renderer.WriteSvg(document!, path, padding: scale);
        var svg = XElement.Load(path);
        double Dimension(string name) => double.Parse(svg.Attribute(name)!.Value, CultureInfo.InvariantCulture) / scale;
        double svgWidth = Dimension("width"), svgHeight = Dimension("height");
        svg.SetAttributeValue("width", svgWidth.ToString("0.########", CultureInfo.InvariantCulture));
        svg.SetAttributeValue("height", svgHeight.ToString("0.########", CultureInfo.InvariantCulture));
        svg.Save(path);
        double top = Math.Min(0, document!.InkBounds.Y);
        result = new(path, svgWidth * 72 / 96, svgHeight * 72 / 96, (document.Baseline - top + scale) / scale * 72 / 96);
        diagnostic = null;
        return true;
    }
}
