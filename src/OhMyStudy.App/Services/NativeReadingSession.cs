using System.Text;
using Microsoft.UI.Xaml;
using OhMyStudy.Domain;

namespace OhMyStudy.App.Services;

/// <summary>All reading surfaces use the NuGet VelociTex renderer.</summary>
internal sealed class NativeReadingSession(double width, bool preview = false, ElementTheme initialTheme = ElementTheme.Default)
{
    private readonly double _width = width;
    private readonly NativeFormulaReadingSession _native = new(width, preview, initialTheme);
    internal NativeReadingSession Nested() => new(Math.Max(80, _width - 12), initialTheme: initialTheme);

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
                string latex = formula.Latex.Trim();
                paragraph.Append(latex.StartsWith('$') || latex.StartsWith(@"\(", StringComparison.Ordinal) || latex.StartsWith(@"\[", StringComparison.Ordinal)
                    ? latex : @"\(" + latex + @"\)");
            }
            else
            {
                if (paragraph.Length > 0) { yield return new TextContent { Text = paragraph.ToString() }; paragraph.Clear(); }
                yield return item;
            }
        }
        if (paragraph.Length > 0) yield return new TextContent { Text = paragraph.ToString() };
    }

    private static IEnumerable<string> Collect(Question question)
    {
        if (question.Points is { } points) yield return $"{points:g} 分";
        foreach (var content in Coalesce(question.Content))
        {
            if (content is TextContent text) yield return text.Text;
            else if (content is TableContent table)
                foreach (var row in table.Rows)
                    foreach (var cell in row) yield return cell;
        }
        foreach (var option in question.Options) yield return option;
        foreach (var feedback in Feedback(question)) yield return feedback;
        foreach (var part in question.Parts)
            foreach (var key in Collect(part)) yield return key;
    }

    internal sealed record FeedbackSection(string Title, string Glyph, IReadOnlyList<string> Texts);
    internal static IEnumerable<FeedbackSection> FeedbackSections(Question question)
    {
        if (question.Attempt is { } attempt)
        {
            yield return new("作答", "\uE70F", new[] { string.IsNullOrWhiteSpace(attempt.StudentAnswer) ? attempt.Grade.Verdict == Verdict.Unanswered ? "未作答" : "未记录作答" : attempt.StudentAnswer });
            if (!string.IsNullOrWhiteSpace(attempt.Grade.Reason)) yield return new("评分说明", "\uE9D9", new[] { attempt.Grade.Reason });
            if (attempt.Teacher is { } teacher && !string.IsNullOrWhiteSpace(teacher.Raw)) yield return new("教师批注", "\uE8F2", new[] { teacher.Raw });
            if (!string.IsNullOrWhiteSpace(attempt.Conflict)) yield return new("判断差异", "\uE7BA", new[] { attempt.Conflict });
            if (!string.IsNullOrWhiteSpace(attempt.AiSolution)) yield return new("解题过程", "\uE8A5", new[] { attempt.AiSolution });
        }
        if (question.Uncertainties.Count > 0) yield return new("待确认", "\uE9CE", question.Uncertainties);
    }
    internal static IEnumerable<string> Feedback(Question question) => FeedbackSections(question).SelectMany(section => section.Texts);
}
