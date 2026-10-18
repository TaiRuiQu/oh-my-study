using System.Text.Json.Serialization;

namespace OhMyStudy.App.Diagnostics;

public sealed class DesktopSmokeReport
{
    public DateTimeOffset TimestampUtc { get; set; }
    public bool IsNativeAot { get; set; }
    public string ProcessArchitecture { get; set; } = "";
    public bool SyntheticDataOnly { get; set; }
    public bool WindowLoaded { get; set; }
    public bool UnifiedTitleBar { get; set; }
    public double TitleBarHeight { get; set; }
    public bool TitleBarPaneToggle { get; set; }
    public bool TitleBarCaptionTheme { get; set; }
    public bool NativeNavigation { get; set; }
    public string[] NavigationDetails { get; set; } = [];
    public string? ScreenshotPath { get; set; }
    public bool ScreenshotCaptured { get; set; }
    public bool CompiledBinding { get; set; }
    public bool TwoWayBinding { get; set; }
    public int TotalItems { get; set; }
    public int RealizedItems { get; set; }
    public bool ItemsStackPanel { get; set; }
    public string? PanelType { get; set; }
    public string? RuntimeClass { get; set; }
    public bool PanelOrientationVertical { get; set; }
    public string? PanelOrientationType { get; set; }
    public string? PanelOrientationValue { get; set; }
    public int RealizedItemsAtEnd { get; set; }
    public bool LastItemRealized { get; set; }
    public bool ScrollRoundTrip { get; set; }
    public bool Virtualization { get; set; }
    public bool PrintSupported { get; set; }
    public bool PrintInterop { get; set; }
    public string? PrintError { get; set; }
    public bool ThemeSwitch { get; set; }
    public bool HighContrastResources { get; set; }
    public bool RotationAndSource { get; set; }
    public bool Cancellation { get; set; }
    public bool PdfRequested { get; set; }
    public bool PdfLoaded { get; set; }
    public int PageCount { get; set; }
    public string? RasterDimensions { get; set; }
    public bool PdfPreview { get; set; }
    public bool PdfPageNavigation { get; set; }
    public bool Passed { get; set; }
    public string? Error { get; set; }
}

[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(DesktopSmokeReport))]
internal partial class SmokeJsonContext : JsonSerializerContext;
