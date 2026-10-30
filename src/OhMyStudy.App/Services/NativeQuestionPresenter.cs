using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.ViewModels;
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
    private const double SectionSpacing = 20;

    public static async Task<FrameworkElement> CreateAsync(Question question, LibraryRepository library, CancellationToken cancellationToken, double availableWidth = 720,
        ElementTheme theme = ElementTheme.Default, string? context = null, Func<Task>? confirmResult = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(library);
        cancellationToken.ThrowIfCancellationRequested();
        // Await without ConfigureAwait(false): all XAML objects belong to this UI thread.
        var reading = await NativeReadingSession.CreateAsync(question, availableWidth, cancellationToken, theme);
        var root = new StackPanel { Spacing = SectionSpacing, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (Overview(question, context) is { } overview) root.Children.Add(overview);
        if (question.Uncertainties.Count > 0)
        {
            var notice = new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false, Title = UiText.Get("Needs review") };
            var messages = new StackPanel { Spacing = 6, Margin = new Thickness(0, 0, 16, 14) };
            foreach (string issue in question.Uncertainties)
                messages.Children.Add(await reading.PresentAsync(UiText.Get(issue), cancellationToken, width: Math.Max(40, reading.Width - 64)));
            notice.Content = messages;
            if (confirmResult is not null)
            {
                var confirm = new Button { Content = UiText.Get("Confirm result") };
                confirm.Click += async (_, _) =>
                {
                    confirm.IsEnabled = false;
                    try { await confirmResult(); }
                    finally { confirm.IsEnabled = true; }
                };
                notice.ActionButton = confirm;
            }
            root.Children.Add(notice);
        }
        root.Children.Add(await QuestionBodyAsync(question, library, reading, cancellationToken));
        if (question.Items.Any(item => item.Attempt is not null))
            root.Children.Add(await AnswersAsync(question, reading, cancellationToken));
        var solutions = SolutionBlocks(question);
        if (solutions.Count > 0) root.Children.Add(AnswerExpander(solutions, question.Items.Count > 1, reading, cancellationToken));
        if (question.KnowledgePoints.Count > 0)
        {
            var topics = new StackPanel { Spacing = 6 };
            topics.Children.Add(LabelRow(UiText.Get("Topics"), "\uE8EC"));
            topics.Children.Add(Text(string.Join(" · ", question.KnowledgePoints)));
            root.Children.Add(topics);
        }
        return root;
    }

    private static FrameworkElement? Overview(Question question, string? context)
    {
        string details = string.Join(" · ", new[] { context, question.Subject, question.QuestionType, question.Items.Count > 1 ? UiText.Format("{0} parts", question.Items.Count) : null }
            .Where(text => !string.IsNullOrWhiteSpace(text)));
        var result = Result(question.Outcome, question.Earned, question.Points, 20);
        if (details.Length == 0 && result is null) return null;
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var meta = new TextBlock { Text = details, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, FontSize = 14, Opacity = .76 };
        grid.Children.Add(meta);
        if (result is not null) { Grid.SetColumn(result, 1); grid.Children.Add(result); }
        return grid;
    }

    private static FrameworkElement? Result(Verdict? verdict, double? earned, double? possible, double scoreSize)
    {
        string score = OutcomeVisuals.Score(earned, possible);
        if (verdict is null && score.Length == 0) return null;
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center };
        if (score.Length != 0) row.Children.Add(new TextBlock { Text = score, FontSize = scoreSize, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        if (verdict is not null) row.Children.Add(Chip(verdict));
        return row;
    }

    private static Border Chip(Verdict? verdict)
    {
        var content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        content.Children.Add(new FontIcon { Glyph = OutcomeVisuals.Glyph(verdict), FontSize = 12, Style = OutcomeVisuals.IconStyle(verdict), VerticalAlignment = VerticalAlignment.Center });
        content.Children.Add(new TextBlock { Text = OutcomeVisuals.Label(verdict), FontSize = 13, VerticalAlignment = VerticalAlignment.Center });
        return new Border { Style = OutcomeVisuals.ChipStyle(verdict), Child = content, VerticalAlignment = VerticalAlignment.Center };
    }

    private static async Task<FrameworkElement> QuestionBodyAsync(Question question, LibraryRepository library, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        var panel = new StackPanel { Spacing = 14, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(await ContentAsync(question.Stem, [], library, reading, cancellationToken));
        foreach (var item in question.Items)
        {
            if (item.Prompt.Count == 0 && item.Options.Count == 0 && string.IsNullOrWhiteSpace(item.Label)) continue;
            panel.Children.Add(await ContentAsync(NativeReadingSession.ItemPrompt(item), item.Options, library, reading, cancellationToken));
        }
        return panel;
    }

    private static async Task<FrameworkElement> AnswersAsync(Question question, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        var body = new StackPanel { Spacing = 12 };
        body.Children.Add(LabelRow(UiText.Get("Your answer"), "\uE70F"));
        var accent = new Border { Width = 3, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Stretch, Margin = new Thickness(0, 2, 10, 2) };
        var card = new Border { CornerRadius = new CornerRadius(8), Padding = new Thickness(14, 12, 14, 14), BorderThickness = new Thickness(1) };
        double inner = reading.Width - accent.Width - accent.Margin.Right - card.Padding.Left - card.Padding.Right;
        bool multi = question.Items.Count > 1;
        foreach (var item in question.Items)
        {
            if (item.Attempt is not { } attempt) continue;
            var block = new StackPanel { Spacing = 6 };
            FrameworkElement? score = Result(attempt.Grade.Verdict, attempt.Grade.Earned, attempt.Grade.Possible, 14);
            var row = new Grid { ColumnSpacing = 14 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            double answerWidth = inner;
            if (score is not null)
            {
                score.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                answerWidth = Math.Max(110, inner - row.ColumnSpacing - Math.Ceiling(score.DesiredSize.Width));
            }
            var answer = new StackPanel { Spacing = 6 };
            string prefix = multi && !string.IsNullOrWhiteSpace(item.Label) ? item.Label.Trim() + "\u2002" : "";
            if (string.IsNullOrWhiteSpace(attempt.StudentAnswer))
                answer.Children.Add(new TextBlock { Text = prefix + (attempt.Grade.Verdict == Verdict.Unanswered ? UiText.Get("Unanswered") : UiText.Get("No response recorded")), FontSize = 15, Opacity = .76, TextWrapping = TextWrapping.Wrap });
            else answer.Children.Add(await reading.PresentAsync(prefix + attempt.StudentAnswer, cancellationToken, width: answerWidth));
            Grid.SetColumn(answer, 0); row.Children.Add(answer);
            if (score is not null) { Grid.SetColumn(score, 1); row.Children.Add(score); }
            block.Children.Add(row);
            string source = attempt.Grade.Source switch { GradeSource.Teacher => UiText.Get("Teacher grading"), GradeSource.Manual => UiText.Get("Manual grading"), GradeSource.AiReference => UiText.Get("Based on reference answers"), _ => UiText.Get("Model grading") };
            var sections = NativeReadingSession.FeedbackSections(new QuestionItem { Key = "", Attempt = attempt }).ToList();
            if (sections.FirstOrDefault()?.Title != UiText.Get("Grading notes"))
                block.Children.Add(new TextBlock { Text = source, FontSize = 12, Opacity = .7 });
            foreach (var section in sections)
            {
                var notes = new StackPanel { Spacing = 5 };
                notes.Children.Add(LabelRow(section.Title == UiText.Get("Grading notes") ? section.Title + " · " + source : section.Title, section.Glyph));
                foreach (string text in section.Texts) notes.Children.Add(await reading.PresentAsync(text, cancellationToken, width: inner));
                block.Children.Add(notes);
            }
            body.Children.Add(block);
        }
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("CardBackgroundFillColorSecondaryBrush", out var fill) && fill is Brush background) card.Background = background;
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out var stroke) && stroke is Brush borderBrush) card.BorderBrush = borderBrush;
        card.Child = body;
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("AccentFillColorDefaultBrush", out var accentFill) && accentFill is Brush accentBrush) accent.Background = accentBrush;
        var layout = new Grid();
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        layout.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(accent, 0); layout.Children.Add(accent);
        Grid.SetColumn(card, 1); layout.Children.Add(card);
        return layout;
    }

    private sealed record SolutionBlock(string Label, string Title, string Glyph, string Text);

    private static List<SolutionBlock> SolutionBlocks(Question question) =>
        question.Items.SelectMany(item => Solutions(item).Select(solution => new SolutionBlock(item.Label.Trim(), solution.Title, solution.Glyph, solution.Text))).ToList();

    private static List<(string Title, string Glyph, string Text)> Solutions(QuestionItem item)
    {
        var result = new List<(string, string, string)>();
        if (!string.IsNullOrWhiteSpace(item.Answer)) result.Add((UiText.Get("Standard answer"), "\uE73E", item.Answer));
        if (!string.IsNullOrWhiteSpace(item.Explanation)) result.Add((UiText.Get("Explanation"), "\uE82F", item.Explanation));
        if (!string.IsNullOrWhiteSpace(item.Attempt?.AiSolution)) result.Add((UiText.Get("Solution steps"), "\uE8A5", item.Attempt!.AiSolution));
        return result;
    }

    private static Expander AnswerExpander(List<SolutionBlock> solutions, bool multi, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        var expander = new Expander
        {
            Header = UiText.Get("Show answer and explanation"),
            Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderBrush = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
            BorderThickness = new Thickness(0),
            Padding = new Thickness(0),
            MinHeight = 36,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        foreach (string key in new[] { "ExpanderHeaderBackground", "ExpanderHeaderBorderBrush", "ExpanderHeaderBorderPointerOverBrush", "ExpanderHeaderBorderPressedBrush" })
            expander.Resources[key] = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        expander.Resources["ExpanderHeaderBorderThickness"] = new Thickness(0);
        expander.Loaded += (_, _) =>
        {
            if (VisualTreeHelper.GetChild(expander, 0) is not Grid grid) return;
            foreach (var header in grid.Children.OfType<Microsoft.UI.Xaml.Controls.Primitives.ToggleButton>())
            {
                header.Padding = new Thickness(0);
                header.HorizontalAlignment = HorizontalAlignment.Left;
            }
        };
        var body = new StackPanel { Spacing = SectionSpacing, Margin = new Thickness(0, 12, 0, 8) };
        expander.Content = body;
        bool loaded = false;
        expander.Expanding += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            try
            {
                string? heading = null;
                foreach (var (label, title, glyph, text) in solutions)
                {
                    if (multi && label.Length > 0 && label != heading)
                    {
                        heading = label;
                        body.Children.Add(new TextBlock { Text = label, FontFamily = new FontFamily(UiText.ReadingFontFamily), FontSize = 14, FontWeight = FontWeights.SemiBold });
                    }
                    var block = new StackPanel { Spacing = 6 };
                    block.Children.Add(LabelRow(title, glyph));
                    block.Children.Add(await reading.PresentAsync(text, cancellationToken));
                    body.Children.Add(block);
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception error) when (error is IOException or InvalidOperationException or COMException)
            { body.Children.Add(Text(UiText.Get("Question content is temporarily unavailable."))); }
        };
        return expander;
    }

    private static async Task<FrameworkElement> ContentAsync(IEnumerable<Content> content, IEnumerable<string> options, LibraryRepository library, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        var panel = new StackPanel { Spacing = ContentSpacing, HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var block in NativeReadingSession.Coalesce(content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (block)
            {
                case TextContent text: panel.Children.Add(await reading.PresentAsync(text.Text, cancellationToken)); break;
                case TableContent table: panel.Children.Add(await TableAsync(table.Rows, reading, cancellationToken)); break;
                case ImageContent image: panel.Children.Add(await SourceImageAsync(image, library, reading, cancellationToken)); break;
                case MermaidContent mermaid: panel.Children.Add(await MermaidAsync(mermaid, reading, cancellationToken)); break;
            }
        }
        foreach (string option in options) panel.Children.Add(await reading.PresentAsync(option, cancellationToken));
        return panel;
    }

    private static StackPanel LabelRow(string label, string glyph)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7, VerticalAlignment = VerticalAlignment.Center, Opacity = .76 };
        row.Children.Add(new FontIcon { Glyph = glyph, FontSize = 13 });
        row.Children.Add(new TextBlock { Text = label, FontFamily = new FontFamily(UiText.ReadingFontFamily), FontSize = 13, FontWeight = FontWeights.SemiBold });
        return row;
    }

    private static TextBlock Text(string text) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontFamily = new FontFamily(UiText.ReadingFontFamily),
        FontSize = DocumentTypography.InterfaceBodySizePoints * 96 / 72,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static async Task<FrameworkElement> TableAsync(List<List<string>> rows, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        var cells = DocumentTable.Layout(rows);
        int columns = cells.Count == 0 ? 0 : cells.Max(cell => cell.Column + cell.ColumnSpan);
        int rowCount = cells.Count == 0 ? 0 : cells.Max(cell => cell.Row + cell.RowSpan);
        for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 72, MaxWidth = 320 });
        for (var row = 0; row < rowCount; row++) grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        foreach (var layoutCell in cells)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var body = new StackPanel { Spacing = 4, MaxWidth = 300 };
            body.Children.Add(await reading.PresentAsync(layoutCell.Text, cancellationToken, table: true));
            var cell = new Border
            {
                Padding = new Thickness(10, 7, 10, 7),
                BorderThickness = new Thickness(1),
                Child = body
            };
            if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out var stroke) && stroke is Brush brush) cell.BorderBrush = brush;
            Grid.SetRow(cell, layoutCell.Row); Grid.SetColumn(cell, layoutCell.Column);
            Grid.SetRowSpan(cell, layoutCell.RowSpan); Grid.SetColumnSpan(cell, layoutCell.ColumnSpan);
            grid.Children.Add(cell);
        }
        AutomationProperties.SetName(grid, UiText.Get("Table"));
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
    }

    private static async Task<FrameworkElement> SourceImageAsync(ImageContent content, LibraryRepository library, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        try
        {
            PageViewportTransform.ValidateRegion(new PageRect(content.Region.X, content.Region.Y, content.Region.Width, content.Region.Height));
            var documents = await library.ListDocumentsAsync(cancellationToken);
            var page = documents.SelectMany(document => document.Pages).SingleOrDefault(page => page.Id == content.Region.PageId);
            if (page is null) return Text(UiText.Get("Image temporarily unavailable."));
            var source = await library.ReadAssetAsync(content.AssetId ?? page.AssetId, cancellationToken: cancellationToken);
            var result = await PageImages.ReadForDisplayAsync(source.Bytes, content.AssetId is null ? content.Region : null, content.AssetId is null ? page.Rotation : (ushort)0, cancellationToken);
            var image = await ImageAsync(Convert.FromBase64String(result.Image.Base64), content.Caption, ContentWidth(reading.Width, content.WidthPercent), cancellationToken);
            if (string.IsNullOrWhiteSpace(content.Caption)) return image;
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(image); panel.Children.Add(await reading.PresentAsync(content.Caption, cancellationToken, width: reading.Width)); return panel;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or COMException or UnauthorizedAccessException)
        { return Text(UiText.Get("Image temporarily unavailable.")); }
    }

    private static async Task<FrameworkElement> MermaidAsync(MermaidContent content, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        try
        {
            var display = await Task.Run(() => MermaidRenderer.RenderDisplay(content.Code), cancellationToken);
            using var stream = new InMemoryRandomAccessStream();
            using (var writer = new DataWriter(stream))
            {
                writer.WriteString(display.Svg); await writer.StoreAsync().AsTask(cancellationToken); writer.DetachStream();
            }
            stream.Seek(0);
            var source = new SvgImageSource();
            await source.SetSourceAsync(stream).AsTask(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var canvas = new Canvas { Width = display.Width, Height = display.Height, Background = new SolidColorBrush(Microsoft.UI.Colors.White) };
            canvas.Children.Add(new UiImage { Source = source, Width = display.Width, Height = display.Height, Stretch = Stretch.Fill });
            foreach (var label in display.Labels)
            {
                var text = new TextBlock
                {
                    Text = label.Text,
                    FontFamily = new FontFamily(UiText.ReadingFontFamily),
                    FontSize = label.FontSize,
                    Foreground = new SolidColorBrush(Microsoft.UI.Colors.Black),
                    TextAlignment = TextAlignment.Center,
                    TextWrapping = TextWrapping.Wrap,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };
                var border = new Border { Width = label.Width, Height = label.Height, Child = text };
                Canvas.SetLeft(border, label.X - label.Width / 2); Canvas.SetTop(border, label.Y - label.Height / 2);
                canvas.Children.Add(border);
            }
            var image = new Viewbox
            {
                Child = canvas,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = ContentWidth(reading.Width, content.WidthPercent),
                MaxHeight = 520
            };
            AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(content.Caption) ? UiText.Get("Diagram") : content.Caption);
            if (string.IsNullOrWhiteSpace(content.Caption)) return image;
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(image); panel.Children.Add(await reading.PresentAsync(content.Caption, cancellationToken, width: reading.Width)); return panel;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error)
        {
            AppLog.Failure("Question.Mermaid", "Render failed for code:\n" + content.Code + "\n" + error);
            return Text(UiText.Get("Question content is temporarily unavailable."));
        }
    }

    private static double ContentWidth(double availableWidth, int? widthPercent) => Math.Max(1, Math.Floor(availableWidth * Math.Clamp(widthPercent ?? 100, 1, 100) / 100d));

    private static async Task<UiImage> ImageAsync(byte[] png, string accessibleText, double availableWidth, CancellationToken cancellationToken)
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
        double pixelWidth = Math.Max(1, bitmap.PixelWidth), pixelHeight = Math.Max(1, bitmap.PixelHeight);
        double scale = Math.Min(availableWidth / pixelWidth, 420 / pixelHeight);
        var image = new UiImage { Source = bitmap, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(1, pixelWidth * scale), Height = Math.Max(1, pixelHeight * scale), MaxHeight = 420 };
        AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(accessibleText) ? UiText.GetByKey("S0437") : accessibleText);
        return image;
    }
}
