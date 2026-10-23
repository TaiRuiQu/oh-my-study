using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private Region? _reviewSourceRegion;

    private async void ViewResults_Click(object sender, RoutedEventArgs e)
    {
        if (_library is not { } library || ViewModel.CurrentDocument is not { } document) return;
        try
        {
            long generation = _documentGeneration;
            var result = await library.GradingResultAsync(document.Id, _lifetime.Token);
            if (_closed || generation != _documentGeneration || ViewModel.CurrentDocument?.Id != document.Id) return;
            var questionNumbers = result.Questions.SelectMany(saved => saved.Question.Items.Select(item => (item.Key, Number: saved.Question.Number + item.Label)))
                .ToDictionary(item => item.Key, item => item.Number, StringComparer.Ordinal);
            var body = new StackPanel { Spacing = 16, MinWidth = 360 };
            body.Children.Add(new TextBlock { Text = UiText.Get("Reference answers"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            Region? destination = null;
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = UiText.GetByKey("S0320"), CloseButtonText = UiText.Get("Close") };
            if (result.References.Count == 0) body.Children.Add(new TextBlock { Text = UiText.Get("No reference answers yet.") });
            foreach (var reference in result.References)
            {
                var card = new StackPanel { Spacing = 8 };
                string match = reference.MatchedItemKey is { } key && questionNumbers.TryGetValue(key, out var number) ? UiText.Format("Matches question {0}", number) : UiText.Get("Not matched yet");
                card.Children.Add(new TextBlock { Text = reference.Number + " · " + match, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                card.Children.Add(ResultText(reference.Answer));
                if (!string.IsNullOrWhiteSpace(reference.Explanation)) card.Children.Add(ResultText(reference.Explanation));
                if (!string.IsNullOrWhiteSpace(reference.UncertainReason)) card.Children.Add(ResultText(UiText.Get("Needs review: ") + reference.UncertainReason));
                if (reference.Sources.FirstOrDefault() is { } source)
                {
                    var locate = new Button { Content = UiText.Get("View answer source page") };
                    locate.Click += (_, _) => { destination = source; dialog.Hide(); }; card.Children.Add(locate);
                }
                body.Children.Add(card);
            }
            body.Children.Add(new TextBlock { Text = UiText.Get("Results by page"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (result.Coverage.Count == 0) body.Children.Add(new TextBlock { Text = UiText.Get("No page results yet.") });
            foreach (var coverage in result.Coverage)
            {
                int page = document.Pages.FindIndex(source => source.Id == coverage.PageId);
                string status = coverage.Status switch { "covered" => UiText.Format("Questions or subquestions checked: {0}", coverage.QuestionKeys.Count), "unreadable" => UiText.Get("Unreadable"), "no_questions" => UiText.Get("No questions"), _ => UiText.Get("Needs review") };
                body.Children.Add(ResultText((page < 0 ? UiText.Get("Source page no longer exists") : UiText.Format("Page {0}", page + 1)) + " · " + status + (string.IsNullOrWhiteSpace(coverage.Reason) ? "" : "\n" + coverage.Reason)));
            }
            dialog.Content = new ScrollViewer { Content = body, MaxHeight = 600 };
            await dialog.ShowAsync();
            if (destination is not null) await LocateReviewedSourceAsync(document, destination);
        }
        catch { ViewModel.StatusMessage = UiText.Get("Could not open results. Try again."); }
    }

    private async Task LocateReviewedSourceAsync(Document document, Region region)
    {
        if (_library is null) return;
        if (ViewModel.CurrentDocument?.Id != document.Id) await LoadDocumentAsync(document);
        SourcePane.Visibility = Visibility.Visible; ApplyWorkspaceLayout();
        long generation = _documentGeneration;
        int index = document.Pages.FindIndex(page => page.Id == region.PageId);
        if (index < 0) { ViewModel.StatusMessage = UiText.Get("Source page no longer exists."); return; }
        if (_sourcePageId != region.PageId) await ShowSourcePageAsync(index);
        if (_closed || generation != _documentGeneration || ViewModel.CurrentDocument?.Id != document.Id || _sourcePageId != region.PageId) return;
        _reviewSourceRegion = region;
        UpdateSourceHighlight();
        var point = PageTransform.TransformPoint(new Windows.Foundation.Point((region.X + region.Width / 2) * _pageWidth, (region.Y + region.Height / 2) * _pageHeight));
        SourceViewer.ChangeView(Math.Max(0, point.X * SourceViewer.ZoomFactor - SourceViewer.ViewportWidth / 2), Math.Max(0, point.Y * SourceViewer.ZoomFactor - SourceViewer.ViewportHeight / 2), null, true);
    }
    private static TextBlock ResultText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
}
