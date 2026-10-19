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
            var questionNumbers = result.Questions.SelectMany(saved => Flatten(saved.Question)).ToDictionary(question => question.DraftKey, question => question.Number, StringComparer.Ordinal);
            var body = new StackPanel { Spacing = 16, MinWidth = 360 };
            body.Children.Add(new TextBlock { Text = "参考答案", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            Region? destination = null;
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "整理结果", CloseButtonText = "关闭" };
            if (result.References.Count == 0) body.Children.Add(new TextBlock { Text = "暂无参考答案。" });
            foreach (var reference in result.References)
            {
                var card = new StackPanel { Spacing = 8 };
                string match = reference.MatchedDraftKey is { } key && questionNumbers.TryGetValue(key, out var number) ? "对应第 " + number + " 题" : "尚未匹配";
                card.Children.Add(new TextBlock { Text = reference.Number + " · " + match, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                card.Children.Add(ResultText(reference.Answer));
                if (!string.IsNullOrWhiteSpace(reference.Explanation)) card.Children.Add(ResultText(reference.Explanation));
                if (!string.IsNullOrWhiteSpace(reference.UncertainReason)) card.Children.Add(ResultText("待确认：" + reference.UncertainReason));
                if (reference.Sources.FirstOrDefault() is { } source)
                {
                    var locate = new Button { Content = "查看答案原页" };
                    locate.Click += (_, _) => { destination = source; dialog.Hide(); }; card.Children.Add(locate);
                }
                body.Children.Add(card);
            }
            body.Children.Add(new TextBlock { Text = "逐页结果", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            if (result.Coverage.Count == 0) body.Children.Add(new TextBlock { Text = "暂无逐页结果。" });
            foreach (var coverage in result.Coverage)
            {
                int page = document.Pages.FindIndex(source => source.Id == coverage.PageId);
                string status = coverage.Status switch { "covered" => "已检查 " + coverage.QuestionKeys.Count + " 个题目或小问", "unreadable" => "无法识别", "no_questions" => "无题目", _ => "待确认" };
                body.Children.Add(ResultText((page < 0 ? "来源页已不存在" : "第 " + (page + 1) + " 页") + " · " + status + (string.IsNullOrWhiteSpace(coverage.Reason) ? "" : "\n" + coverage.Reason)));
            }
            dialog.Content = new ScrollViewer { Content = body, MaxHeight = 600 };
            await dialog.ShowAsync();
            if (destination is not null) await LocateReviewedSourceAsync(document, destination);
        }
        catch { ViewModel.StatusMessage = "整理结果暂时无法打开，请重试。"; }
    }

    private async Task LocateReviewedSourceAsync(Document document, Region region)
    {
        if (_library is null) return;
        if (ViewModel.CurrentDocument?.Id != document.Id) await LoadDocumentAsync(document);
        SourcePane.Visibility = Visibility.Visible; ApplyWorkspaceLayout();
        long generation = _documentGeneration;
        int index = document.Pages.FindIndex(page => page.Id == region.PageId);
        if (index < 0) { ViewModel.StatusMessage = "来源页已不存在。"; return; }
        if (_sourcePageId != region.PageId) await ShowSourcePageAsync(index);
        if (_closed || generation != _documentGeneration || ViewModel.CurrentDocument?.Id != document.Id || _sourcePageId != region.PageId) return;
        _reviewSourceRegion = region;
        UpdateSourceHighlight();
        var point = PageTransform.TransformPoint(new Windows.Foundation.Point((region.X + region.Width / 2) * _pageWidth, (region.Y + region.Height / 2) * _pageHeight));
        SourceViewer.ChangeView(Math.Max(0, point.X * SourceViewer.ZoomFactor - SourceViewer.ViewportWidth / 2), Math.Max(0, point.Y * SourceViewer.ZoomFactor - SourceViewer.ViewportHeight / 2), null, true);
    }
    private static TextBlock ResultText(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true };
    private static IEnumerable<Question> Flatten(Question question) => new[] { question }.Concat(question.Parts.SelectMany(Flatten));
}
