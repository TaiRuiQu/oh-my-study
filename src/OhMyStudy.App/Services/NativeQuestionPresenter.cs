using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OhMyStudy.Documents;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;
using System.Runtime.InteropServices;
using Windows.Storage.Streams;
using UiImage = Microsoft.UI.Xaml.Controls.Image;

namespace OhMyStudy.App.Services;

/// <summary>Creates native reading controls on the caller's UI thread; image and formula work is bounded off-thread.</summary>
public static class NativeQuestionPresenter
{
    private static readonly NativePageImages PageImages = new();
    private const double ContentSpacing = 10;
    // Section titles sit one comfortable step above body text: 10 stack spacing + 14 margin.
    private const double SectionSpacing = 14;


    public static async Task<FrameworkElement> CreateAsync(Question question, LibraryRepository library, CancellationToken cancellationToken, double availableWidth = 720, ElementTheme theme = ElementTheme.Default)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(library);
        cancellationToken.ThrowIfCancellationRequested();
        // Await without ConfigureAwait(false): all XAML objects belong to this UI thread.
        var panel = new StackPanel { Spacing = ContentSpacing, HorizontalAlignment = HorizontalAlignment.Stretch };
        var reading = await NativeReadingSession.CreateAsync(question, availableWidth, cancellationToken, theme);
        await AddQuestionAsync(panel, question, library, reading, cancellationToken, root: true);
        return panel;
    }

    private static async Task AddQuestionAsync(StackPanel panel, Question question, LibraryRepository library, NativeReadingSession reading, CancellationToken cancellationToken, bool root = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var label = question.Points is { } points ? $"{question.Number}　（{points:g} 分）" : question.Number;
        if (!root) panel.Children.Add(Text(label, heading: true));
        else if (question.Points is { } total)
        { panel.Children.Add(LabelRow("满分 " + total.ToString("g") + " 分", "\uE9D9")); }
        foreach (var content in NativeReadingSession.Coalesce(question.Content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (content)
            {
                case TextContent text: panel.Children.Add(await reading.PresentAsync(text.Text, cancellationToken)); break;
                case FormulaContent formula: panel.Children.Add(await reading.PresentAsync("\\(" + formula.Latex + "\\)", cancellationToken)); break;
                case TableContent table: panel.Children.Add(await TableAsync(table.Rows, reading, cancellationToken)); break;
                case ImageContent image: panel.Children.Add(await SourceImageAsync(image, library, cancellationToken)); break;
            }
        }
        foreach (var option in question.Options) panel.Children.Add(await reading.PresentAsync(option, cancellationToken));
        StackPanel? scorePanel = null;
        if (question.Attempt is { } attempt)
        {
            var score = new StackPanel { Spacing = ContentSpacing, Margin = new Thickness(0, SectionSpacing, 0, 0) };
            score.Children.Add(LabelRow("评分", "\uE9D9"));
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            row.Children.Add(new TextBlock { Text = attempt.Grade.Earned is { } earned && attempt.Grade.Possible is { } possible ? $"{earned:g} / {possible:g}" : "分数未确定", FontSize = 22, FontWeight = FontWeights.SemiBold });
            string verdict = attempt.Grade.Verdict switch { Verdict.Correct => "正确", Verdict.Partial => "部分正确", Verdict.Incorrect => "错误", Verdict.Unanswered => "未作答", _ => "待确认" };
            row.Children.Add(LabelRow(verdict, attempt.Grade.Verdict == Verdict.Correct ? "\uE73E" : attempt.Grade.Verdict == Verdict.Incorrect ? "\uE711" : attempt.Grade.Verdict == Verdict.Partial ? "\uE7BA" : "\uE9CE"));
            score.Children.Add(row);
            string source = attempt.Grade.Source switch { GradeSource.Teacher => "教师评分", GradeSource.Manual => "手动评分", GradeSource.AiReference => "依据参考答案", _ => "模型评分" };
            score.Children.Add(LabelRow(source, "\uE77B"));
            scorePanel = score;
        }
        foreach (var section in NativeReadingSession.FeedbackSections(question))
        {
            if (section.Title == "评分说明" && scorePanel is not null)
            { foreach (string text in section.Texts) scorePanel.Children.Add(await reading.PresentAsync(text, cancellationToken)); continue; }
            var body = new StackPanel { Spacing = ContentSpacing, Margin = new Thickness(0, SectionSpacing, 0, 0) };
            body.Children.Add(LabelRow(section.Title, section.Glyph));
            foreach (string text in section.Texts) body.Children.Add(await reading.PresentAsync(text, cancellationToken));
            panel.Children.Add(body);
            if (section.Title == "作答" && scorePanel is not null) panel.Children.Add(scorePanel);
        }
        foreach (var part in question.Parts)
        {
            var nested = new StackPanel { Spacing = ContentSpacing, Margin = new Thickness(16, SectionSpacing, 0, 0) };
            await AddQuestionAsync(nested, part, library, reading.Nested(), cancellationToken);
            panel.Children.Add(nested);
        }
    }

    private static StackPanel LabelRow(string label, string glyph)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center, Opacity = .76 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        row.Children.Add(new TextBlock { Text = label, FontFamily = new FontFamily(DocumentTypography.InterfaceFontFamily), FontSize = 13, FontWeight = FontWeights.SemiBold });
        return row;
    }

    private static TextBlock Text(string text, bool heading = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontFamily = new FontFamily(DocumentTypography.InterfaceFontFamily),
        FontSize = heading ? 16 : DocumentTypography.InterfaceBodySizePoints * 96 / 72,
        FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static async Task<FrameworkElement> TableAsync(List<List<string>> rows, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        int columns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 72, MaxWidth = 320 });
        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < columns; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = new StackPanel { Spacing = 4, MaxWidth = 300 };
                body.Children.Add(await reading.PresentAsync(column < rows[row].Count ? rows[row][column] : "", cancellationToken, table: true));
                var cell = new Border
                {
                    Padding = new Thickness(10, 7, 10, 7),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = body
                };
                if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out var stroke) && stroke is Brush brush) cell.BorderBrush = brush;
                Grid.SetRow(cell, row); Grid.SetColumn(cell, column); grid.Children.Add(cell);
            }
        }
        AutomationProperties.SetName(grid, "表格");
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
    }

    private static async Task<FrameworkElement> SourceImageAsync(ImageContent content, LibraryRepository library, CancellationToken cancellationToken)
    {
        try
        {
            PageViewportTransform.ValidateRegion(new PageRect(content.Region.X, content.Region.Y, content.Region.Width, content.Region.Height));
            var documents = await library.ListDocumentsAsync(cancellationToken);
            var page = documents.SelectMany(document => document.Pages).SingleOrDefault(page => page.Id == content.Region.PageId);
            if (page is null) return Text("图片暂时无法显示。");
            var source = await library.ReadAssetAsync(page.AssetId, cancellationToken: cancellationToken);
            var result = await PageImages.ReadAsync(source.Bytes, content.Region, page.Rotation, cancellationToken);
            var image = await ImageAsync(Convert.FromBase64String(result.Image.Base64), content.Caption, cancellationToken);
            image.MaxHeight = 420;
            if (string.IsNullOrWhiteSpace(content.Caption)) return image;
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(image); panel.Children.Add(Text(content.Caption)); return panel;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or COMException or UnauthorizedAccessException)
        { return Text("图片暂时无法显示。"); }
    }

    private static async Task<UiImage> ImageAsync(byte[] png, string accessibleText, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png); await writer.StoreAsync().AsTask(cancellationToken); writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var image = new UiImage { Source = bitmap, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 720 };
        AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(accessibleText) ? "题目图片" : accessibleText);
        return image;
    }
}
