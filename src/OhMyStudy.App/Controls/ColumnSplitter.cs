using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Windows.System;

namespace OhMyStudy.App.Controls;

public sealed partial class ColumnSplitter : Control
{
    private double? lastPosition;
    public event EventHandler<double>? ResizeRequested;
    public static readonly DependencyProperty OrientationProperty = DependencyProperty.Register(nameof(Orientation), typeof(Orientation), typeof(ColumnSplitter), new PropertyMetadata(Orientation.Vertical, OrientationChanged));
    public Orientation Orientation { get => (Orientation)GetValue(OrientationProperty); set => SetValue(OrientationProperty, value); }
    public double MinimumBefore { get; set; } = 140;
    public double MinimumAfter { get; set; } = 140;
    public double? ColumnSize { get; private set; }
    public double? RowSize { get; private set; }

    public ColumnSplitter()
    {
        ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
        PointerPressed += Pressed;
        PointerMoved += Moved;
        PointerReleased += Released;
        PointerCaptureLost += (_, _) => { lastPosition = null; VisualStateManager.GoToState(this, "Normal", false); };
        PointerEntered += (_, _) => VisualStateManager.GoToState(this, lastPosition is null ? "PointerOver" : "Pressed", false);
        PointerExited += (_, _) => { if (lastPosition is null) VisualStateManager.GoToState(this, "Normal", false); };
        KeyDown += KeyPressed;
        Loaded += (_, _) => UpdateOrientation();
    }

    private static void OrientationChanged(DependencyObject sender, DependencyPropertyChangedEventArgs e) => ((ColumnSplitter)sender).UpdateOrientation();

    protected override void OnApplyTemplate() { base.OnApplyTemplate(); UpdateOrientation(); }

    private void UpdateOrientation()
    {
        bool rows = Orientation == Orientation.Horizontal;
        ProtectedCursor = InputSystemCursor.Create(rows ? InputSystemCursorShape.SizeNorthSouth : InputSystemCursorShape.SizeWestEast);
        if (GetTemplateChild("Divider") is FrameworkElement divider)
        {
            divider.Width = rows ? double.NaN : 1;
            divider.Height = rows ? 1 : double.NaN;
            divider.HorizontalAlignment = rows ? HorizontalAlignment.Stretch : HorizontalAlignment.Center;
            divider.VerticalAlignment = rows ? VerticalAlignment.Center : VerticalAlignment.Stretch;
        }
    }

    public void Resize(double delta)
    {
        if (ResizeRequested is { } resize) { resize(this, delta); return; }
        if (Parent is not Grid grid) return;
        bool rows = Orientation == Orientation.Horizontal;
        int index = rows ? Grid.GetRow(this) : Grid.GetColumn(this);
        int count = rows ? grid.RowDefinitions.Count : grid.ColumnDefinitions.Count;
        if (index < 1 || index + 1 >= count) return;
        double before = rows ? grid.RowDefinitions[index - 1].ActualHeight : grid.ColumnDefinitions[index - 1].ActualWidth;
        double after = rows ? grid.RowDefinitions[index + 1].ActualHeight : grid.ColumnDefinitions[index + 1].ActualWidth;
        double total = before + after;
        if (total < MinimumBefore + MinimumAfter) return;
        double size = Math.Clamp(before + delta, MinimumBefore, total - MinimumAfter);
        if (rows)
        {
            RowSize = size;
            grid.RowDefinitions[index - 1].Height = new GridLength(size);
            grid.RowDefinitions[index + 1].Height = new GridLength(1, GridUnitType.Star);
        }
        else
        {
            ColumnSize = size;
            grid.ColumnDefinitions[index - 1].Width = new GridLength(size);
            grid.ColumnDefinitions[index + 1].Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private void KeyPressed(object sender, KeyRoutedEventArgs e)
    {
        if (e.Handled) return;
        bool rows = Orientation == Orientation.Horizontal;
        double step = e.Key == VirtualKey.Home ? -double.MaxValue : e.Key == VirtualKey.End ? double.MaxValue
            : e.Key == (rows ? VirtualKey.Up : VirtualKey.Left) ? -16 : e.Key == (rows ? VirtualKey.Down : VirtualKey.Right) ? 16 : 0;
        if (step == 0) return;
        Resize(step);
        e.Handled = true;
    }

    private void Pressed(object sender, PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Parent as UIElement);
        if (!point.IsInContact || !CapturePointer(e.Pointer)) return;
        lastPosition = Orientation == Orientation.Horizontal ? point.Position.Y : point.Position.X;
        Focus(FocusState.Pointer);
        VisualStateManager.GoToState(this, "Pressed", false);
        e.Handled = true;
    }

    private void Moved(object sender, PointerRoutedEventArgs e)
    {
        if (lastPosition is not { } previous) return;
        var point = e.GetCurrentPoint(Parent as UIElement).Position;
        double current = Orientation == Orientation.Horizontal ? point.Y : point.X;
        lastPosition = current;
        Resize(current - previous);
        e.Handled = true;
    }

    private void Released(object sender, PointerRoutedEventArgs e)
    {
        if (lastPosition is null) return;
        lastPosition = null;
        ReleasePointerCapture(e.Pointer);
        VisualStateManager.GoToState(this, "Normal", false);
        e.Handled = true;
    }
}
