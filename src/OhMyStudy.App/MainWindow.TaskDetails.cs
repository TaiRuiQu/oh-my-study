using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Domain;
using Windows.Storage.Streams;
using UiImage = Microsoft.UI.Xaml.Controls.Image;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task<BitmapImage> LoadTaskImageAsync(string assetId)
    {
        var library = _library ?? throw new InvalidOperationException("The library is closed.");
        var asset = await library.ReadAssetAsync(assetId, cancellationToken: _lifetime.Token);
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(asset.Bytes);
            await writer.StoreAsync().AsTask(_lifetime.Token);
            writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream).AsTask(_lifetime.Token);
        return bitmap;
    }

    private string TaskImageTitle(JobActivityImage image)
    {
        int index = _selectedTask?.Input?.Document.Pages.FindIndex(page => page.Id == image.PageId) ?? -1;
        string title = index >= 0 ? UiText.Format(image.Crop is null ? "Page {0}" : "Page {0} crop", index + 1) : UiText.Get("Image preview");
        if (image.Crop is { } crop)
            title += " · " + UiText.Format("Region: left {0:0.#}%, top {1:0.#}%, width {2:0.#}%, height {3:0.#}%", crop.X * 100, crop.Y * 100, crop.Width * 100, crop.Height * 100);
        if (image.Rotation != 0) title += " · " + UiText.Format("Rotation: {0}°", image.Rotation);
        return title;
    }

    private sealed class TaskLogEntryView
    {
        private readonly MainWindow _owner;
        private readonly FontIcon _icon = new() { FontSize = 14, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock _heading = new() { FontFamily = new FontFamily(UiText.InterfaceFontFamily), TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 22 };
        private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 16, LineHeight = 26, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _output = new() { FontFamily = new FontFamily(UiText.InterfaceFontFamily), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 16, LineHeight = 26, Margin = new Thickness(0, 6, 0, 0) };
        private readonly FontIcon _chevron = new() { Glyph = "\uE76C", FontSize = 12, RenderTransform = new TranslateTransform { Y = -2 } };
        private readonly ToggleButton _toggle = new() { Width = 24, Height = 28, Padding = new Thickness(4), VerticalAlignment = VerticalAlignment.Center, Opacity = 0 };
        private readonly Grid _header = new() { ColumnSpacing = 2, HorizontalAlignment = HorizontalAlignment.Left, Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
        private readonly StackPanel _details = new() { Spacing = 12, Margin = new Thickness(24, 8, 0, 0), Visibility = Visibility.Collapsed };
        private readonly Dictionary<string, TaskStepDetailsView> _members = [];
        private IReadOnlyList<JobActivity> _activities = [];
        private Func<string, Brush>? _brush;
        private bool _headerHovered;
        public Grid Root { get; } = new() { ColumnSpacing = 8, Padding = new Thickness(0, 0, 0, 8) };

        public TaskLogEntryView(MainWindow owner)
        {
            _owner = owner;
            Root.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
            Root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 4; i++) Root.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Root.Children.Add(_icon);
            _header.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            _header.ColumnDefinitions.Add(new() { Width = GridLength.Auto });
            _heading.VerticalAlignment = VerticalAlignment.Center;
            _header.Children.Add(_heading);
            Grid.SetColumn(_header, 1); Root.Children.Add(_header);
            Grid.SetRow(_detail, 1); Grid.SetColumnSpan(_detail, 2); Root.Children.Add(_detail);
            Grid.SetRow(_output, 2); Grid.SetColumnSpan(_output, 2); Root.Children.Add(_output);
            _toggle.Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["TaskDisclosureButtonStyle"];
            _toggle.Content = _chevron;
            _toggle.Checked += (_, _) => { _owner._taskFollowLatest = false; ApplyExpansion(); };
            _toggle.Unchecked += (_, _) => ApplyExpansion();
            _toggle.GotFocus += (_, _) => UpdateDisclosureVisibility();
            _toggle.LostFocus += (_, _) => UpdateDisclosureVisibility();
            _header.PointerEntered += (_, _) => { _headerHovered = true; UpdateDisclosureVisibility(); };
            _header.PointerExited += (_, _) => { _headerHovered = false; UpdateDisclosureVisibility(); };
            _heading.Tapped += (_, args) =>
            {
                if (_toggle.Visibility != Visibility.Visible) return;
                _toggle.IsChecked = _toggle.IsChecked != true;
                args.Handled = true;
            };
            Grid.SetColumn(_toggle, 1); _header.Children.Add(_toggle);
            Grid.SetRow(_details, 3); Grid.SetColumnSpan(_details, 2); Root.Children.Add(_details);
        }

        private void UpdateDisclosureVisibility()
            => _toggle.Opacity = _headerHovered || _toggle.IsChecked == true || _toggle.FocusState == FocusState.Keyboard ? 1 : 0;

        private void ApplyExpansion()
        {
            bool expanded = _toggle.IsChecked == true && _toggle.Visibility == Visibility.Visible;
            _chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
            _details.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
            UpdateDisclosureVisibility();
            AutomationProperties.SetName(_toggle, UiText.Get(expanded ? "Hide step details" : "Show step details"));
            if (!expanded || _brush is null) return;
            var keys = _activities.Select(activity => activity.Key).ToHashSet(StringComparer.Ordinal);
            foreach (string stale in _members.Keys.Where(key => !keys.Contains(key)).ToArray())
            { _details.Children.Remove(_members[stale].Root); _members.Remove(stale); }
            for (int index = 0; index < _activities.Count; index++)
            {
                var activity = _activities[index];
                if (!_members.TryGetValue(activity.Key, out var member))
                { member = new TaskStepDetailsView(_owner); _members.Add(activity.Key, member); }
                if (index >= _details.Children.Count || !ReferenceEquals(_details.Children[index], member.Root))
                { _details.Children.Remove(member.Root); _details.Children.Insert(index, member.Root); }
                member.Update(activity, _activities.Count > 1, _brush);
            }
        }

        public void Update(JobActivity step, string state, ulong time, Func<string, Brush> brush, string? tooltip, IReadOnlyList<JobActivity> members)
        {
            _activities = members; _brush = brush;
            bool model = step.Kind == "model";
            bool error = state is "failed" or "cancelled";
            var secondary = brush("TaskSecondaryBrush");
            _icon.Foreground = error ? brush("TaskErrorBrush") : secondary;
            _icon.Glyph = error ? "\uE7BA" : step.Kind switch
            { "model" => "\uE945", "read_page" or "read_pages" => "\uE8A5", "save_question" or "save_questions" => "\uE74E", "save_references" => "\uE8F1", "finish" => "\uE73E", "python" or "shell" => "\uE8EF", _ => "\uE9F9" };
            string title = model ? UiText.Get("Recognition") : members.Count == 1 && step.Kind == "python" ? UiText.Get("Run Python")
                : members.Count == 1 && step.Kind == "shell" ? UiText.Get("Run PowerShell") : UiText.ActivityText(step.Title);
            if (model && step.Text.Length >= 16000) title += UiText.Get(" (output excerpt)");
            string stateLabel = state switch { "completed" => UiText.Get("Completed"), "failed" => UiText.Get("Failed"), "cancelled" => UiText.Get("Canceled"), "waiting" => UiText.Get("Awaiting confirmation"), _ => UiText.Get("In progress") };
            _heading.Text = title + (state == "completed" ? "" : "  " + stateLabel);
            _heading.Foreground = error ? brush("TaskErrorBrush") : secondary;
            _detail.Text = UiText.ActivityText(step.Detail);
            _detail.Foreground = error ? brush("TaskErrorBrush") : secondary;
            _detail.Visibility = (model || step.Kind == "state") && !string.IsNullOrWhiteSpace(step.Detail) && (error || step.Text.Length == 0) ? Visibility.Visible : Visibility.Collapsed;
            _output.Text = model ? step.Text.TrimEnd() : "";
            _output.Visibility = _output.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _toggle.Foreground = secondary;
            _toggle.Visibility = model || step.Kind == "state" ? Visibility.Collapsed : Visibility.Visible;
            ApplyExpansion();
            ToolTipService.SetToolTip(_heading, tooltip ?? (model && step.Text.Length >= 16000 ? UiText.Get("Output excerpt") : null));
            AutomationProperties.SetHelpText(Root, tooltip ?? "");
            AutomationProperties.SetName(Root, _heading.Text);
        }
    }

    private sealed class TaskStepDetailsView
    {
        private readonly MainWindow _owner;
        private readonly TextBlock _title = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, FontSize = 14, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _detail = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        private readonly TextBlock _codeLabel = new() { FontSize = 14 };
        private readonly TextBlock _code = new() { FontFamily = new FontFamily("Consolas"), FontSize = 13, IsTextSelectionEnabled = true };
        private readonly ScrollViewer _codeScroll = new() { MaxHeight = 360, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        private readonly TextBlock _outputLabel = new() { Text = UiText.Get("Output"), FontSize = 14 };
        private readonly TextBlock _output = new() { FontFamily = new FontFamily("Consolas"), FontSize = 13, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
        private readonly StackPanel _imageList = new() { Spacing = 8 };
        private readonly Dictionary<string, StackPanel> _images = [];
        public StackPanel Root { get; } = new() { Spacing = 6 };

        public TaskStepDetailsView(MainWindow owner)
        {
            _owner = owner;
            _codeScroll.Content = _code;
            foreach (var child in new FrameworkElement[] { _title, _detail, _codeLabel, _codeScroll, _outputLabel, _output, _imageList }) Root.Children.Add(child);
        }

        public void Update(JobActivity activity, bool showTitle, Func<string, Brush> brush)
        {
            _title.Text = activity.Kind switch { "python" => UiText.Get("Run Python"), "shell" => UiText.Get("Run PowerShell"), _ => UiText.ActivityText(activity.Title) };
            _title.Visibility = showTitle ? Visibility.Visible : Visibility.Collapsed;
            _detail.Text = UiText.ActivityText(activity.Detail);
            _detail.Visibility = string.IsNullOrWhiteSpace(activity.Detail) ? Visibility.Collapsed : Visibility.Visible;
            _codeLabel.Text = UiText.Get(activity.Kind == "shell" ? "PowerShell code" : "Python code");
            _code.Text = activity.Code;
            _codeLabel.Visibility = _codeScroll.Visibility = activity.Code.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            _output.Text = activity.Text.TrimEnd();
            _outputLabel.Visibility = _output.Visibility = _output.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var foreground = brush(activity.State is "failed" or "cancelled" ? "TaskErrorBrush" : "TaskSecondaryBrush");
            foreach (var text in new[] { _title, _detail, _codeLabel, _code, _outputLabel, _output }) text.Foreground = foreground;
            for (int index = 0; index < activity.Images.Count; index++)
            {
                var image = activity.Images[index];
                string key = index + ":" + image.AssetId;
                if (_images.ContainsKey(key)) continue;
                var caption = new TextBlock { Text = _owner.TaskImageTitle(image), FontSize = 14, TextWrapping = TextWrapping.Wrap, Foreground = foreground };
                var preview = new UiImage { Stretch = Stretch.Uniform, MaxHeight = 420, HorizontalAlignment = HorizontalAlignment.Left };
                AutomationProperties.SetName(preview, caption.Text);
                var panel = new StackPanel { Spacing = 4 };
                panel.Children.Add(caption); panel.Children.Add(preview);
                _images.Add(key, panel); _imageList.Children.Add(panel);
                _ = LoadImageAsync(image, preview, panel);
            }
        }

        private async Task LoadImageAsync(JobActivityImage image, UiImage preview, StackPanel panel)
        {
            try { preview.Source = await _owner.LoadTaskImageAsync(image.AssetId); }
            catch (OperationCanceledException) when (_owner._lifetime.IsCancellationRequested) { }
            catch (Exception error)
            {
                AppLog.Failure("Task.ImagePreview", $"Asset={image.AssetId}; Page={image.PageId}; {error}");
                panel.Children.Add(new TextBlock { Text = UiText.Get("Preview unavailable"), FontSize = 14, TextWrapping = TextWrapping.Wrap });
            }
        }
    }
}
