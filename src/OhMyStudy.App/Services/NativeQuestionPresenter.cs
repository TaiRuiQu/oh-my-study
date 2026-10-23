using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
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
    private const double CardInset = 34;

    public static async Task<FrameworkElement> CreateAsync(Question question, LibraryRepository library, CancellationToken cancellationToken, double availableWidth = 720,
        ElementTheme theme = ElementTheme.Default, string? context = null, int selectedItem = 0, Action<int>? selectionChanged = null)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(library);
        cancellationToken.ThrowIfCancellationRequested();
        // Await without ConfigureAwait(false): all XAML objects belong to this UI thread.
        var reading = await NativeReadingSession.CreateAsync(question, availableWidth, cancellationToken, theme);
        var root = new StackPanel { Spacing = SectionSpacing, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (Overview(question, context) is { } overview) root.Children.Add(overview);
        if (question.Uncertainties.Count > 0)
            root.Children.Add(new InfoBar { Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false, Title = UiText.Get("Needs review"), Message = string.Join("\n", question.Uncertainties) });
        root.Children.Add(await ContentAsync(question.Stem, [], library, reading, cancellationToken));
        int initial = Math.Clamp(selectedItem, 0, question.Items.Count - 1);
        var itemHost = new ContentControl { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, IsTabStop = false };
        var rendered = new Dictionary<int, FrameworkElement>();
        int requested = initial;
        async Task ShowAsync(int index)
        {
            requested = index;
            if (!rendered.TryGetValue(index, out var body)) rendered[index] = body = await ItemAsync(question, index, library, reading, cancellationToken);
            if (requested == index) itemHost.Content = body;
        }
        if (question.Items.Count > 1)
            root.Children.Add(Selector(question, initial, async index =>
            {
                selectionChanged?.Invoke(index);
                try { await ShowAsync(index); }
                catch (OperationCanceledException) { }
            }));
        root.Children.Add(itemHost);
        await ShowAsync(initial);
        if (question.KnowledgePoints.Count > 0)
        {
            var topics = new StackPanel { Spacing = 6 };
            topics.Children.Add(LabelRow(UiText.Get("Topics"), "\uE8EC"));
            topics.Children.Add(Text(string.Join(" · ", question.KnowledgePoints)));
            root.Children.Add(topics);
        }
        return root;
    }

    public static int FirstMistake(Question question) => Math.Max(0, question.Items.FindIndex(item => item.Attempt?.Grade.Verdict is Verdict.Partial or Verdict.Incorrect or Verdict.Unanswered));

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

    private static FrameworkElement Selector(Question question, int initial, Action<int> select)
    {
        var bar = new SelectorBar();
        AutomationProperties.SetName(bar, UiText.Get("Subquestions"));
        for (int index = 0; index < question.Items.Count; index++)
        {
            var item = question.Items[index];
            var verdict = item.Attempt?.Grade.Verdict;
            var entry = new SelectorBarItem { Text = item.Label };
            if (verdict is not null) entry.Icon = new FontIcon { Glyph = OutcomeVisuals.Glyph(verdict), Style = OutcomeVisuals.IconStyle(verdict) };
            AutomationProperties.SetName(entry, verdict is null ? item.Label : item.Label + ", " + OutcomeVisuals.Label(verdict));
            bar.Items.Add(entry);
        }
        bar.SelectedItem = bar.Items[initial];
        bar.SelectionChanged += (sender, _) =>
        {
            int index = sender.SelectedItem is null ? -1 : sender.Items.IndexOf(sender.SelectedItem);
            if (index >= 0) select(index);
        };
        return new ScrollViewer
        {
            Content = bar,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollMode = ScrollMode.Enabled,
            VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Disabled,
            Margin = new Thickness(-4, 0, 0, -8)
        };
    }

    private static async Task<FrameworkElement> ItemAsync(Question question, int index, LibraryRepository library, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        var item = question.Items[index];
        var panel = new StackPanel { Spacing = 16, HorizontalAlignment = HorizontalAlignment.Stretch };
        if (question.Items.Count > 1)
        {
            var header = new Grid { ColumnSpacing = 16 };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, VerticalAlignment = VerticalAlignment.Center };
            title.Children.Add(new TextBlock { Text = item.Label, FontSize = 18, FontWeight = FontWeights.SemiBold, FontFamily = new FontFamily(UiText.ReadingFontFamily) });
            if (item.Points is { } points && item.Attempt is null)
                title.Children.Add(new TextBlock { Text = UiText.Format("{0:g} points", points), FontSize = 14, Opacity = .76, VerticalAlignment = VerticalAlignment.Center });
            header.Children.Add(title);
            if (Result(item.Attempt?.Grade.Verdict, item.Attempt?.Grade.Earned, item.Attempt is null ? null : item.Points, 16) is { } result)
            { Grid.SetColumn(result, 1); header.Children.Add(result); }
            panel.Children.Add(header);
        }
        if (item.Prompt.Count > 0 || item.Options.Count > 0) panel.Children.Add(await ContentAsync(item.Prompt, item.Options, library, reading, cancellationToken));
        if (item.Attempt is { } attempt) panel.Children.Add(await ResponseAsync(attempt, reading.WithWidth(reading.Width - CardInset), cancellationToken));
        if (Solutions(item) is { Count: > 0 } solutions) panel.Children.Add(AnswerExpander(solutions, reading.WithWidth(reading.Width - CardInset), cancellationToken));
        return panel;
    }

    private static async Task<FrameworkElement> ResponseAsync(Attempt attempt, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        var body = new StackPanel { Spacing = ContentSpacing };
        body.Children.Add(LabelRow(UiText.Get("Your answer"), "\uE70F"));
        if (string.IsNullOrWhiteSpace(attempt.StudentAnswer))
            body.Children.Add(new TextBlock { Text = attempt.Grade.Verdict == Verdict.Unanswered ? UiText.Get("Unanswered") : UiText.Get("No response recorded"), FontSize = 15, Opacity = .76, TextWrapping = TextWrapping.Wrap });
        else body.Children.Add(await reading.PresentAsync(attempt.StudentAnswer, cancellationToken));
        string source = attempt.Grade.Source switch { GradeSource.Teacher => UiText.Get("Teacher grading"), GradeSource.Manual => UiText.Get("Manual grading"), GradeSource.AiReference => UiText.Get("Based on reference answers"), _ => UiText.Get("Model grading") };
        var sections = NativeReadingSession.FeedbackSections(new QuestionItem { Key = "", Attempt = attempt }).ToList();
        if (sections.FirstOrDefault()?.Title != UiText.Get("Grading notes"))
            body.Children.Add(new TextBlock { Text = source, FontSize = 13, Opacity = .76, Margin = new Thickness(0, SectionSpacing / 2, 0, 0) });
        foreach (var section in sections)
        {
            var block = new StackPanel { Spacing = 6, Margin = new Thickness(0, SectionSpacing / 2, 0, 0) };
            block.Children.Add(LabelRow(section.Title == UiText.Get("Grading notes") ? section.Title + " · " + source : section.Title, section.Glyph));
            foreach (string text in section.Texts) block.Children.Add(await reading.PresentAsync(text, cancellationToken));
            body.Children.Add(block);
        }
        return new Border { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["ResponseCardStyle"], Child = body };
    }

    private static List<(string Title, string Glyph, string Text)> Solutions(QuestionItem item)
    {
        var result = new List<(string, string, string)>();
        if (!string.IsNullOrWhiteSpace(item.Answer)) result.Add((UiText.Get("Standard answer"), "\uE73E", item.Answer));
        if (!string.IsNullOrWhiteSpace(item.Explanation)) result.Add((UiText.Get("Explanation"), "\uE82F", item.Explanation));
        if (!string.IsNullOrWhiteSpace(item.Attempt?.AiSolution)) result.Add((UiText.Get("Solution steps"), "\uE8A5", item.Attempt!.AiSolution));
        return result;
    }

    private static Expander AnswerExpander(List<(string Title, string Glyph, string Text)> solutions, NativeReadingSession reading, CancellationToken cancellationToken)
    {
        var expander = new Expander
        {
            Header = UiText.Get("Show answer and explanation"),
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch
        };
        var body = new StackPanel { Spacing = SectionSpacing };
        expander.Content = body;
        bool loaded = false;
        expander.Expanding += async (_, _) =>
        {
            if (loaded) return;
            loaded = true;
            try
            {
                foreach (var (title, glyph, text) in solutions)
                {
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
                case ImageContent image: panel.Children.Add(await SourceImageAsync(image, library, cancellationToken)); break;
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
        AutomationProperties.SetName(grid, UiText.Get("Table"));
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
    }

    private static async Task<FrameworkElement> SourceImageAsync(ImageContent content, LibraryRepository library, CancellationToken cancellationToken)
    {
        try
        {
            PageViewportTransform.ValidateRegion(new PageRect(content.Region.X, content.Region.Y, content.Region.Width, content.Region.Height));
            var documents = await library.ListDocumentsAsync(cancellationToken);
            var page = documents.SelectMany(document => document.Pages).SingleOrDefault(page => page.Id == content.Region.PageId);
            if (page is null) return Text(UiText.Get("Image temporarily unavailable."));
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
        { return Text(UiText.Get("Image temporarily unavailable.")); }
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
        AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(accessibleText) ? UiText.GetByKey("S0437") : accessibleText);
        return image;
    }
}
