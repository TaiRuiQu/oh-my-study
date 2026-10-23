using System.Text;

namespace OhMyStudy.Domain;

public static class QuestionText
{
    public static string Delimit(string latex)
    {
        string trimmed = latex.Trim();
        return trimmed.StartsWith('$') || trimmed.StartsWith(@"\(", StringComparison.Ordinal) || trimmed.StartsWith(@"\[", StringComparison.Ordinal)
            ? trimmed : @"\(" + latex + @"\)";
    }

    public static string Reading(IEnumerable<Content> content) => string.Concat(content.Select(item => item switch
    {
        TextContent text => text.Text,
        FormulaContent formula => Delimit(formula.Latex),
        TableContent table => " " + string.Join("　", table.Rows.Take(2).SelectMany(row => row)),
        ImageContent image => image.Caption,
        _ => ""
    }));

    public static string Preview(Question question, int maximumLength = 320)
    {
        var text = new StringBuilder(Reading(question.Stem.Take(8)).Trim());
        foreach (var item in question.Items)
        {
            if (text.Length >= maximumLength) break;
            string prompt = Reading(item.Prompt.Take(4)).Trim();
            if (prompt.Length != 0) text.Append(text.Length == 0 ? "" : " ").Append(item.Label).Append(prompt);
        }
        return Cut(text.ToString(), maximumLength);
    }

    public static string Search(Question question)
    {
        var text = new StringBuilder();
        void Add(string? value) { if (!string.IsNullOrWhiteSpace(value)) text.Append(value).Append('\n'); }
        Add(question.Number); Add(question.Subject); Add(question.QuestionType);
        Add(Plain(question.Stem));
        foreach (string point in question.KnowledgePoints) Add(point);
        foreach (var item in question.Items)
        {
            Add(item.Label); Add(Plain(item.Prompt)); Add(item.Answer); Add(item.Explanation);
            foreach (string option in item.Options) Add(option);
        }
        Add(question.Notes);
        return text.Length > 20_000 ? text.ToString(0, 20_000) : text.ToString();
    }

    public static string Plain(IEnumerable<Content> content) => string.Join("\n", content.Select(item => item switch
    {
        TextContent text => text.Text,
        FormulaContent formula => formula.Latex,
        TableContent table => string.Join("\n", table.Rows.Select(row => string.Join("  ", row))),
        ImageContent image => image.Caption,
        _ => ""
    }));

    private static string Cut(string text, int maximumLength)
    {
        if (text.Length <= maximumLength) return text;
        string prefix = text[..maximumLength];
        int open = prefix.LastIndexOf(@"\(", StringComparison.Ordinal), close = prefix.LastIndexOf(@"\)", StringComparison.Ordinal);
        if (open > close) prefix = prefix[..open];
        int dollars = prefix.Count(c => c == '$');
        if (dollars % 2 == 1) prefix = prefix[..prefix.LastIndexOf('$')];
        return prefix.TrimEnd() + "…";
    }
}
