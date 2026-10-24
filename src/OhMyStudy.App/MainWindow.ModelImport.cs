using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private sealed record ModelSelection(ContentDialog Dialog, HashSet<string> Selected, TextBox Search, ListView List);
    private ModelSelection CreateModelImportDialog(List<ModelConfig> candidates)
    {
        var selected = new HashSet<string>(StringComparer.Ordinal);
        double width = Math.Max(200, Math.Min(480, RootGrid.ActualWidth - 96));
        var search = new TextBox { PlaceholderText = UiText.Get("Search model name or ID") };
        var count = new TextBlock();
        var list = new ListView { SelectionMode = ListViewSelectionMode.Multiple, MaxHeight = Math.Max(140, Math.Min(360, RootGrid.ActualHeight - 330)), HorizontalContentAlignment = HorizontalAlignment.Stretch };
        // A compiled template avoids reflection and keeps each row inside the fixed dialog width.
        list.ItemTemplate = (DataTemplate)RootGrid.Resources["ImportModelTemplate"];
        var body = new StackPanel { Spacing = 12, Width = width }; body.Children.Add(search); body.Children.Add(count); body.Children.Add(list);
        var empty = new TextBlock { Text = UiText.Get("No models found. Try different keywords."), TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed }; body.Children.Add(empty);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = UiText.Get("Select models to add"), Content = body, PrimaryButtonText = UiText.Get("Add"), CloseButtonText = UiText.Get("Cancel"), IsPrimaryButtonEnabled = false };
        bool filtering = false;
        void UpdateCount() { count.Text = UiText.Format("Models: {0} · Selected: {1}", list.Items.Count, selected.Count); dialog.IsPrimaryButtonEnabled = selected.Count > 0; }
        void Filter()
        {
            filtering = true;
            try
            {
                string query = search.Text.Trim();
                list.ItemsSource = candidates.Where(m => m.Id.Contains(query, StringComparison.OrdinalIgnoreCase) || m.Label.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();
                foreach (var item in list.Items.OfType<ModelConfig>()) if (selected.Contains(item.Id)) list.SelectedItems.Add(item);
                empty.Visibility = list.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
                list.Visibility = list.Items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
            }
            finally { filtering = false; UpdateCount(); }
        }
        search.TextChanged += (_, _) => Filter();
        list.SelectionChanged += (_, args) =>
        {
            if (filtering) return;
            foreach (var item in args.AddedItems.OfType<ModelConfig>()) selected.Add(item.Id);
            foreach (var item in args.RemovedItems.OfType<ModelConfig>()) selected.Remove(item.Id);
            UpdateCount();
        };
        Filter(); return new(dialog, selected, search, list);
    }

    private static ComboBox CreateProcessingChoice(string header, double width) => new()
    {
        Header = header,
        Width = width,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };
}
