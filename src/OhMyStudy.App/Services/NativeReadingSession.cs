using System.Text;
using Microsoft.UI.Xaml;
using OhMyStudy.Domain;

namespace OhMyStudy.App.Services;

/// <summary>All reading surfaces use the NuGet VelociTex renderer.</summary>
internal sealed class NativeReadingSession(double width, bool preview = false, ElementTheme initialTheme = ElementTheme.Default)
{
    private readonly NativeFormulaReadingSession _native = new(width, preview, initialTheme);

    public static async Task<NativeReadingSession> CreateAsync(Question question, double availableWidth, CancellationToken token, ElementTheme theme = ElementTheme.Default)
    {
        var session = new NativeReadingSession(Math.Clamp(Math.Floor(availableWidth), 80, 4000), initialTheme: theme);
        await NativeFormulaReadingSession.PrepareAsync(Collect(question), false, token);
        return session;
    }

    internal static async Task<NativeReadingSession> CreatePreviewsAsync(IReadOnlyList<string> texts, double availableWidth, CancellationToken token)
    {
        var session = new NativeReadingSession(Math.Clamp(Math.Floor(availableWidth), 40, 1200), preview: true);
        await NativeFormulaReadingSession.PrepareAsync(texts, true, token);
        return session;
    }

    internal static double? CachedPreviewHeight(string text, double availableWidth)
        => NativeFormulaReadingSession.CachedPreviewHeight(text, Math.Clamp(Math.Floor(availableWidth), 40, 1200));

    public Task<FrameworkElement> PresentAsync(string text, CancellationToken token, bool table = false, ElementTheme? theme = null)
        => _native.PresentAsync(text, token, table, theme);

    internal static IEnumerable<Content> Coalesce(IEnumerable<Content> content)
    {
        var paragraph = new StringBuilder();
        foreach (var item in content)
        {
            if (item is TextContent text) paragraph.Append(text.Text);
            else if (item is FormulaContent formula)
            {
                paragraph.Append(QuestionText.Delimit(formula.Latex));
            }
            else
            {
                if (paragraph.Length > 0) { yield return new TextContent { Text = paragraph.ToString() }; paragraph.Clear(); }
                yield return item;
            }
        }
        if (paragraph.Length > 0) yield return new TextContent { Text = paragraph.ToString() };
    }

    internal static IEnumerable<Content> ItemPrompt(QuestionItem item)
    {
        if (!string.IsNullOrWhiteSpace(item.Label))
        {
            string points = item.Attempt is null && item.Points is { } possible ? " (" + UiText.Format("{0:g} points", possible) + ")" : "";
            yield return new TextContent { Text = item.Label.Trim() + points + " " };
        }
        foreach (var content in item.Prompt) yield return content;
    }

    private static IEnumerable<string> Collect(Question question)
    {
        foreach (string text in Texts(question.Stem)) yield return text;
        foreach (var item in question.Items)
        {
            if (item.Points is { } points) yield return UiText.Format("{0:g} points", points);
            foreach (string text in Texts(ItemPrompt(item))) yield return text;
            foreach (string option in item.Options) yield return option;
            foreach (string feedback in FeedbackSections(item).SelectMany(section => section.Texts)) yield return feedback;
            if (!string.IsNullOrWhiteSpace(item.Answer)) yield return item.Answer;
            if (!string.IsNullOrWhiteSpace(item.Explanation)) yield return item.Explanation;
        }
        foreach (string issue in question.Uncertainties) yield return issue;
    }

    private static IEnumerable<string> Texts(IEnumerable<Content> content)
    {
        foreach (var block in Coalesce(content))
        {
            if (block is TextContent text) yield return text.Text;
            else if (block is TableContent table)
                foreach (var row in table.Rows)
                    foreach (var cell in row) yield return cell;
        }
    }

    internal sealed record FeedbackSection(string Title, string Glyph, IReadOnlyList<string> Texts);
    internal static IEnumerable<FeedbackSection> FeedbackSections(QuestionItem item)
    {
        if (item.Attempt is not { } attempt) yield break;
        if (!string.IsNullOrWhiteSpace(attempt.Grade.Reason)) yield return new(UiText.Get("Grading notes"), "\uE9D9", new[] { attempt.Grade.Reason });
        if (attempt.Teacher is { } teacher && !string.IsNullOrWhiteSpace(teacher.Raw)) yield return new(UiText.Get("Teacher annotations"), "\uE8F2", new[] { teacher.Raw });
        if (!string.IsNullOrWhiteSpace(attempt.Conflict)) yield return new(UiText.Get("Grading differences"), "\uE7BA", new[] { attempt.Conflict });
    }
}
