using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Graphics.DirectX;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Automation.Provider;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using OhMyStudy.App.Diagnostics;
using VelociTex;
using VelociTex.Rendering.Direct2D;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;

namespace OhMyStudy.App.Controls;

/// <summary>Host-owned DirectWrite paragraph layout with native formula inline objects.</summary>
internal sealed unsafe partial class NativeFormulaParagraph : Grid
{
    internal readonly record struct Part(string Text, MathDocument? Formula);
    private readonly string _source;
    private readonly IReadOnlyList<Part> _parts;
    private readonly double _availableWidth, _fontSize;
    private readonly bool _preview;
    private readonly List<nint> _objects = [];
    private readonly List<PreparedMath> _prepared = [];
    private readonly List<FormulaPlacement> _placements = [];
    private readonly List<float> _lineBaselines = [];
    private readonly List<float> _glyphBaselines = [];
    private WindowsMathRenderer? _renderer;
    private nint _factory, _format, _layout, _device;
    private GCHandle _handle;
    private CompositionGraphicsDevice? _graphics;
    private CompositionDrawingSurface? _surface;
    private CompositionSurfaceBrush? _brush;
    private SpriteVisual? _visual;
    private XamlRoot? _root;
    private double _scale = 1;
    private Exception? _callbackError;
    private nint _drawTarget, _drawBrush;
    private MathDrawingColor _ink;
    private double _fullHeight;
    private static readonly nint InlineTable = MakeInlineTable(), RendererTable = MakeRendererTable();
    [ThreadStatic] private static WindowsMathRenderer? SharedRenderer;
    [ThreadStatic] private static int SharedRendererOwners;
    [ThreadStatic] private static nint SharedDevice;
    [ThreadStatic] private static CompositionGraphicsDevice? SharedGraphics;

    internal NativeFormulaParagraph(string source, IReadOnlyList<Part> parts, double width, double fontSize, bool preview)
    {
        _source = source; _parts = parts; _availableWidth = Math.Clamp(width, 40, 4000); _fontSize = fontSize; _preview = preview;
        HorizontalAlignment = HorizontalAlignment.Left; IsTabStop = !preview;
        var copy = new KeyboardAccelerator { Key = VirtualKey.C, Modifiers = VirtualKeyModifiers.Control, ScopeOwner = this };
        copy.Invoked += (_, args) => { Copy(_source); args.Handled = true; }; KeyboardAccelerators.Add(copy);
        var menu = new MenuFlyout(); var all = new MenuFlyoutItem { Text = UiText.Get("复制") }; all.Click += (_, _) => Copy(_source); menu.Items.Add(all);
        int index = 0;
        foreach (var part in parts.Where(part => part.Formula is not null))
        {
            var item = new MenuFlyoutItem { Text = UiText.Format("复制公式 {0}", ++index) }; string formula = part.Formula!.Source;
            item.Click += (_, _) => Copy(formula); menu.Items.Add(item);
        }
        if (!preview) ContextFlyout = menu;
        // Measure the actual layout before attachment. Loaded rebuilds native resources after virtualization.
        try { BuildLayout(); } finally { ReleaseLayout(); }
        Loaded += (_, _) => { _root = XamlRoot; if (_root is not null) _root.Changed += RootChanged; TryRebuild(); };
        Unloaded += (_, _) => { if (_root is not null) _root.Changed -= RootChanged; _root = null; Release(); };
        ActualThemeChanged += (_, _) => { if (IsLoaded) TryRedraw(); };
    }
    private static void Copy(string text) { DataPackage data = new(); data.SetText(text); Clipboard.SetContent(data); }
    private void RootChanged(XamlRoot sender, XamlRootChangedEventArgs args) { if (Math.Abs(sender.RasterizationScale - _scale) > .0001) TryRebuild(); }
    private void TryRebuild()
    {
        try { Rebuild(); }
        catch (Exception error) { AppLog.Error("Question.NativeParagraph", error); Release(); Children.Clear(); Children.Add(new TextBlock { Text = _source, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = !_preview, FontSize = _fontSize }); }
    }
    private void TryRedraw() { try { Redraw(); } catch (Exception error) { AppLog.Error("Question.NativeParagraph", error); TryRebuild(); } }
    private void BuildLayout()
    {
        Guid iid = new("b859ee5a-d838-4b5b-a2e8-1adc7d93db48"); nint factory = 0, format = 0, layout = 0; Check(DWriteCreateFactory(0, &iid, &factory)); _factory = factory;
        fixed (char* family = UiText.ReadingFontFamily) fixed (char* locale = UiText.Language)
            Check(((delegate* unmanaged[Stdcall]<nint, char*, nint, uint, uint, uint, float, char*, nint*, int>)Slot(_factory, 15))(_factory, family, 0, 400, 0, 5, (float)_fontSize, locale, &format));
        _format = format;
        StringBuilder text = new(); var ranges = new List<(int Position, MathDocument Document)>();
        foreach (var part in _parts)
        {
            if (part.Formula is null) text.Append(part.Text);
            else { ranges.Add((text.Length, part.Formula)); text.Append('\uFFFC'); }
        }
        string value = text.ToString(); if (value.Length == 0) value = " ";
        fixed (char* characters = value) Check(((delegate* unmanaged[Stdcall]<nint, char*, uint, nint, float, float, nint*, int>)Slot(_factory, 18))(_factory, characters, (uint)value.Length, _format, (float)_availableWidth, 32768, &layout)); _layout = layout;
        _handle = GCHandle.Alloc(this);
        foreach (var range in ranges)
        {
            MathDocument doc = range.Document;
            double left = Math.Min(0, doc.InkBounds.X), top = Math.Min(0, doc.InkBounds.Y);
            double width = Math.Max(doc.LogicalSize.Width, doc.InkBounds.X + doc.InkBounds.Width) - left;
            double height = Math.Max(doc.LogicalSize.Height, doc.InkBounds.Y + doc.InkBounds.Height) - top;
            Callback* inline = (Callback*)NativeMemory.AllocZeroed((nuint)sizeof(Callback));
            *inline = new() { Vtable = (nint*)InlineTable, State = GCHandle.ToIntPtr(_handle), References = 1, Index = _objects.Count, Metrics = new() { Width = (float)(width + 2), Height = (float)(height + 2), Baseline = (float)(doc.Baseline - top + 1) } };
            _objects.Add((nint)inline);
            Check(((delegate* unmanaged[Stdcall]<nint, nint, TextRange, int>)Slot(_layout, 39))(_layout, (nint)inline, new((uint)range.Position, 1)));
        }
        TextMetrics metrics = default; Check(((delegate* unmanaged[Stdcall]<nint, TextMetrics*, int>)Slot(_layout, 60))(_layout, &metrics));
        LineMetrics[] lines = new LineMetrics[Math.Max(1, (int)metrics.LineCount)]; uint count = 0;
        fixed (LineMetrics* measured = lines) Check(((delegate* unmanaged[Stdcall]<nint, LineMetrics*, uint, uint*, int>)Slot(_layout, 59))(_layout, measured, (uint)lines.Length, &count));
        _lineBaselines.Clear(); float lineTop = 1;
        foreach (var line in lines) { _lineBaselines.Add(lineTop + line.Baseline); lineTop += line.Height; }
        _fullHeight = Math.Ceiling(metrics.Height) + 2;
        Width = Math.Max(1, Math.Ceiling(Math.Max(metrics.WidthIncludingTrailingWhitespace, Math.Min(_availableWidth, metrics.Width))) + 2);
        if (_preview && _fullHeight > 48)
        {
            double shown = 0; int visible = 0;
            foreach (var line in lines) { if (shown + line.Height + 2 > 48) break; shown += line.Height; visible++; if (visible == 2) break; }
            if (visible == 0) throw new InvalidOperationException("The first preview line is taller than the bounded preview.");
            Height = Math.Max(_fontSize + 2, Math.Ceiling(shown) + 2);
        }
        else Height = _preview ? _fullHeight : Math.Max(_fontSize + 2, _fullHeight);
        if (_preview)
        {
            Width = Math.Min(Width, _availableWidth + 2);
            Check(((delegate* unmanaged[Stdcall]<nint, float, int>)Slot(_layout, 29))(_layout, (float)(Height - 2)));
            nint ellipsis = 0;
            try
            {
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(_factory, 20))(_factory, _format, &ellipsis));
                Trimming trim = new() { Granularity = 1 };
                Check(((delegate* unmanaged[Stdcall]<nint, Trimming*, nint, int>)Slot(_layout, 9))(_layout, &trim, ellipsis));
            }
            finally { Free(ref ellipsis); }
        }
        if (Width > 8192 || Height > 8192) throw new InvalidOperationException("Paragraph exceeds the native surface boundary.");
    }
    private void Rebuild(double? density = null)
    {
        Release(); BuildLayout(); _scale = density ?? XamlRoot?.RasterizationScale ?? 1;
        if (!double.IsFinite(_scale) || _scale <= 0 || _scale > 8) throw new ArgumentOutOfRangeException(nameof(density));
        if (Width * Height * _scale * _scale > 4_194_304) throw new InvalidOperationException("Paragraph exceeds the native surface budget.");
        _renderer = SharedRenderer ??= new WindowsMathRenderer(); SharedRendererOwners++;
        foreach (var part in _parts) if (part.Formula is not null) _prepared.Add(_renderer.Prepare(part.Formula));
        Compositor compositor = ElementCompositionPreview.GetElementVisual(this).Compositor;
        if (SharedDevice == 0) SharedDevice = WindowsMathRenderer.CreateWarpD2DDevice();
        _device = SharedDevice; Marshal.AddRef(_device);
        if (SharedGraphics is null)
        {
            nint pointer = WinRT.MarshalInspectable<Compositor>.FromManaged(compositor), interop = 0, graphics = 0;
            try
            {
                Guid iid = new("FAB19398-6D19-4D8A-B752-8F096C396069"); Check(Marshal.QueryInterface(pointer, in iid, out interop));
                Check(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(interop, 3))(interop, _device, &graphics));
                SharedGraphics = WinRT.MarshalInspectable<CompositionGraphicsDevice>.FromAbi(graphics);
            }
            finally { Free(ref pointer); Free(ref interop); Free(ref graphics); }
        }
        _graphics = SharedGraphics;
        _surface = _graphics.CreateDrawingSurface(new Windows.Foundation.Size(Math.Ceiling(Width * _scale), Math.Ceiling(Height * _scale)), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        _brush = compositor.CreateSurfaceBrush(_surface); _visual = compositor.CreateSpriteVisual(); _visual.Size = new((float)Width, (float)Height); _visual.Brush = _brush;
        ElementCompositionPreview.SetElementChildVisual(this, _visual); Redraw();
    }
    private void Redraw()
    {
        if (_surface is null || _renderer is null) return;
        var highContrast = new Windows.UI.ViewManagement.AccessibilitySettings(); var colors = new Windows.UI.ViewManagement.UISettings();
        Windows.UI.Color color = highContrast.HighContrast ? colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground) : ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;
        _ink = new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
        nint pointer = WinRT.MarshalInspectable<CompositionDrawingSurface>.FromManaged(_surface), interop = 0, context = 0; bool begun = false;
        try
        {
            Guid iid = new("2D6355C2-AD57-4EAE-92E4-4C3EFF65D578"); Check(Marshal.QueryInterface(pointer, in iid, out interop));
            iid = new("E8F7FE7A-191C-466D-AD95-975678BDA998"); PixelPoint offset;
            Check(((delegate* unmanaged[Stdcall]<nint, void*, Guid*, nint*, PixelPoint*, int>)Slot(interop, 3))(interop, null, &iid, &context, &offset)); begun = true;
            ((delegate* unmanaged[Stdcall]<nint, float, float, void>)Slot(context, 51))(context, (float)(96 * _scale), (float)(96 * _scale));
            Matrix matrix = new() { M11 = 1, M22 = 1, Dx = (float)(offset.X / _scale), Dy = (float)(offset.Y / _scale) };
            ((delegate* unmanaged[Stdcall]<nint, Matrix*, void>)Slot(context, 30))(context, &matrix);
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)Slot(context, 47))(context, 0);
            Color nativeColor = new() { R = _ink.R, G = _ink.G, B = _ink.B, A = _ink.A };
            nint drawBrush = 0; Check(((delegate* unmanaged[Stdcall]<nint, Color*, nint, nint*, int>)Slot(context, 8))(context, &nativeColor, 0, &drawBrush)); _drawBrush = drawBrush;
            _drawTarget = context; _callbackError = null; _placements.Clear(); _glyphBaselines.Clear();
            Callback renderer = new() { Vtable = (nint*)RendererTable, State = GCHandle.ToIntPtr(_handle), References = 1, Index = -1 };
            Check(((delegate* unmanaged[Stdcall]<nint, nint, nint, float, float, int>)Slot(_layout, 58))(_layout, 0, (nint)(&renderer), 1, 1));
            if (_callbackError is not null) throw new InvalidOperationException("Native paragraph drawing failed.", _callbackError);
            if (!BaselineChecksPassed) throw new InvalidOperationException("Body and formula origins must match the actual DirectWrite line baselines.");
        }
        finally
        {
            _drawTarget = 0; Free(ref _drawBrush); Free(ref context);
            try { if (begun) Check(((delegate* unmanaged[Stdcall]<nint, int>)Slot(interop, 4))(interop)); }
            finally { Free(ref interop); Free(ref pointer); }
        }
    }
    private void ReleaseLayout()
    {
        Free(ref _layout);
        foreach (nint value in _objects) NativeMemory.Free((void*)value); _objects.Clear();
        if (_handle.IsAllocated) _handle.Free(); Free(ref _format); Free(ref _factory);
    }
    private void Release()
    {
        ElementCompositionPreview.SetElementChildVisual(this, null);
        _visual?.Dispose(); _visual = null; _brush?.Dispose(); _brush = null; _surface?.Dispose(); _surface = null; _graphics = null;
        foreach (var prepared in _prepared) prepared.Dispose(); _prepared.Clear();
        Free(ref _device);
        if (_renderer is not null && --SharedRendererOwners == 0) { SharedGraphics?.Dispose(); SharedGraphics = null; SharedRenderer?.Dispose(); SharedRenderer = null; Free(ref SharedDevice); }
        _renderer = null; ReleaseLayout();
    }
    protected override AutomationPeer OnCreateAutomationPeer() => new ParagraphPeer(this);
    private sealed partial class ParagraphPeer(NativeFormulaParagraph owner) : FrameworkElementAutomationPeer(owner), IValueProvider
    {
        protected override string GetClassNameCore() => nameof(NativeFormulaParagraph);
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetNameCore() => owner._source;
        protected override string GetHelpTextCore() => UiText.Get("按 Ctrl+C 复制全部内容；菜单可复制单个公式。");
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.Value ? this : base.GetPatternCore(pattern);
        protected override IList<AutomationPeer> GetChildrenCore()
        {
            var children = new List<AutomationPeer>();
            foreach (var part in owner._parts) if (part.Formula is { } formula) children.Add(new FormulaPeer(formula));
            return children;
        }
        public bool IsReadOnly => true; public string Value => owner._source;
        public void SetValue(string value) => throw new InvalidOperationException(UiText.Get("题目内容不可在此修改。"));
    }
    private sealed partial class FormulaPeer(MathDocument formula) : AutomationPeer, IValueProvider
    {
        protected override string GetClassNameCore() => "Formula";
        protected override AutomationControlType GetAutomationControlTypeCore() => AutomationControlType.Text;
        protected override string GetNameCore() => formula.Source;
        protected override string GetLocalizedControlTypeCore() => UiText.Get("公式");
        protected override string GetHelpTextCore() => UiText.Get("在题目菜单中复制此公式。");
        protected override object GetPatternCore(PatternInterface pattern) => pattern == PatternInterface.Value ? this : base.GetPatternCore(pattern);
        public bool IsReadOnly => true; public string Value => formula.Source;
        public void SetValue(string value) => throw new InvalidOperationException(UiText.Get("公式不可在此修改。"));
    }
    internal int FormulaCount => _parts.Count(part => part.Formula is not null);
    internal string Source => _source;
    internal bool NativeSurfaceReady => _surface is not null;
    internal IReadOnlyList<string> FormulaSources => _parts.Where(part => part.Formula is not null).Select(part => part.Formula!.Source).ToArray();
    internal bool BaselineChecksPassed => (_preview || _placements.Count == FormulaCount) && _placements.All(item => _lineBaselines.Any(line => Math.Abs(line - item.Y - item.Baseline) < .02f)) && _glyphBaselines.All(value => _lineBaselines.Any(line => Math.Abs(line - value) < .02f));
    internal IReadOnlyList<float> LineBaselines => _lineBaselines;
    internal void RebuildForAcceptance(double density) => Rebuild(density);
    internal IReadOnlyList<FormulaPlacement> Placements => _placements;
    internal readonly record struct FormulaPlacement(int Index, float X, float Y, float Baseline);
    private static nint MakeInlineTable()
    {
        nint* table = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(NativeFormulaParagraph), 7 * sizeof(nint));
        table[0] = (nint)(delegate* unmanaged[Stdcall]<Callback*, Guid*, nint*, int>)&Query; table[1] = (nint)(delegate* unmanaged[Stdcall]<Callback*, uint>)&AddRef; table[2] = (nint)(delegate* unmanaged[Stdcall]<Callback*, uint>)&ReleaseCallback;
        table[3] = (nint)(delegate* unmanaged[Stdcall]<Callback*, nint, nint, float, float, int, int, nint, int>)&InlineDraw; table[4] = (nint)(delegate* unmanaged[Stdcall]<Callback*, InlineMetrics*, int>)&GetMetrics; table[5] = (nint)(delegate* unmanaged[Stdcall]<Callback*, Overhang*, int>)&GetOverhang; table[6] = (nint)(delegate* unmanaged[Stdcall]<Callback*, uint*, uint*, int>)&GetBreak; return (nint)table;
    }
    private static nint MakeRendererTable()
    {
        nint* table = (nint*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(NativeFormulaParagraph), 10 * sizeof(nint)); nint* inline = (nint*)InlineTable;
        table[0] = inline[0]; table[1] = inline[1]; table[2] = inline[2]; table[3] = (nint)(delegate* unmanaged[Stdcall]<Callback*, nint, int*, int>)&PixelSnapping; table[4] = (nint)(delegate* unmanaged[Stdcall]<Callback*, nint, Matrix*, int>)&CurrentTransform; table[5] = (nint)(delegate* unmanaged[Stdcall]<Callback*, nint, float*, int>)&PixelsPerDip; table[6] = (nint)(delegate* unmanaged[Stdcall]<Callback*, nint, float, float, uint, nint, nint, nint, int>)&GlyphDraw; table[7] = (nint)(delegate* unmanaged[Stdcall]<Callback*, nint, float, float, nint, nint, int>)&Decoration; table[8] = table[7]; table[9] = (nint)(delegate* unmanaged[Stdcall]<Callback*, nint, float, float, nint, int, int, nint, int>)&RendererInlineDraw; return (nint)table;
    }
    private static NativeFormulaParagraph Owner(Callback* self) => (NativeFormulaParagraph)GCHandle.FromIntPtr(self->State).Target!;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Query(Callback* self, Guid* iid, nint* result)
    {
        if (iid == null || result == null) return unchecked((int)0x80004003);
        bool match = *iid == new Guid("00000000-0000-0000-c000-000000000046") || (self->Index >= 0 && *iid == new Guid("8339fde3-106f-47ab-8373-1c6295eb10b3")) || (self->Index < 0 && (*iid == new Guid("ef8a8135-5cc6-45fe-8825-c5a0724eb819") || *iid == new Guid("eaf3a2da-ecf4-4d24-b644-b34f6842024b")));
        if (!match) { *result = 0; return unchecked((int)0x80004002); }
        *result = (nint)self; self->References++; return 0;
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static uint AddRef(Callback* self) => ++self->References;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static uint ReleaseCallback(Callback* self) => --self->References;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int GetMetrics(Callback* self, InlineMetrics* metrics) { *metrics = self->Metrics; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int GetOverhang(Callback* self, Overhang* metrics) { *metrics = default; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int GetBreak(Callback* self, uint* before, uint* after) { *before = 0; *after = 0; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int InlineDraw(Callback* self, nint context, nint renderer, float x, float y, int sideways, int rtl, nint effect)
    {
        NativeFormulaParagraph owner = Owner(self);
        try
        {
            MathDocument doc = owner._prepared[self->Index].Document;
            owner._placements.Add(new(self->Index, x, y, self->Metrics.Baseline));
            owner._renderer!.Draw(owner._prepared[self->Index], owner._drawTarget, owner._ink, x + 1 - Math.Min(0, doc.InkBounds.X), y + 1 - Math.Min(0, doc.InkBounds.Y)); return 0;
        }
        catch (Exception error) { owner._callbackError = error; return unchecked((int)0x80004005); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int PixelSnapping(Callback* self, nint context, int* disabled) { *disabled = 1; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int CurrentTransform(Callback* self, nint context, Matrix* matrix) { *matrix = new() { M11 = 1, M22 = 1 }; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int PixelsPerDip(Callback* self, nint context, float* value) { *value = (float)Owner(self)._scale; return 0; }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int GlyphDraw(Callback* self, nint context, float x, float y, uint mode, nint run, nint description, nint effect)
    {
        NativeFormulaParagraph owner = Owner(self);
        try { owner._glyphBaselines.Add(y); ((delegate* unmanaged[Stdcall]<nint, Point, nint, nint, uint, void>)Slot(owner._drawTarget, 29))(owner._drawTarget, new() { X = x, Y = y }, run, owner._drawBrush, mode); return 0; }
        catch (Exception error) { owner._callbackError = error; return unchecked((int)0x80004005); }
    }
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int Decoration(Callback* self, nint context, float x, float y, nint decoration, nint effect) => 0;
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])] private static int RendererInlineDraw(Callback* self, nint context, float x, float y, nint inline, int sideways, int rtl, nint effect) => ((delegate* unmanaged[Stdcall]<nint, nint, nint, float, float, int, int, nint, int>)Slot(inline, 3))(inline, context, (nint)self, x, y, sideways, rtl, effect);
    private static nint Slot(nint value, int index) => (*(nint**)value)[index];
    private static void Check(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    private static void Free(ref nint value) { nint old = value; value = 0; if (old != 0) Marshal.Release(old); }
    [LibraryImport("dwrite.dll")] private static partial int DWriteCreateFactory(uint kind, Guid* iid, nint* factory);
    [StructLayout(LayoutKind.Sequential)] private struct Callback { internal nint* Vtable; internal nint State; internal uint References; internal int Index; internal InlineMetrics Metrics; }
    [StructLayout(LayoutKind.Sequential)] private struct InlineMetrics { internal float Width, Height, Baseline; internal int Sideways; }
    [StructLayout(LayoutKind.Sequential)] private struct Overhang { internal float Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct TextRange(uint start, uint length) { internal uint Start = start, Length = length; }
    [StructLayout(LayoutKind.Sequential)] private struct TextMetrics { internal float Left, Top, Width, WidthIncludingTrailingWhitespace, Height, LayoutWidth, LayoutHeight; internal uint MaxBidiDepth, LineCount; }
    [StructLayout(LayoutKind.Sequential)] private struct LineMetrics { internal uint Length, TrailingWhitespace, Newline; internal float Height, Baseline; internal int Trimmed; }
    [StructLayout(LayoutKind.Sequential)] private struct Trimming { internal uint Granularity, Delimiter, DelimiterCount; }
    [StructLayout(LayoutKind.Sequential)] private struct Point { internal float X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct PixelPoint { internal int X, Y; }
    [StructLayout(LayoutKind.Sequential)] private struct Matrix { internal float M11, M12, M21, M22, Dx, Dy; }
    [StructLayout(LayoutKind.Sequential)] private struct Color { internal float R, G, B, A; }
}
