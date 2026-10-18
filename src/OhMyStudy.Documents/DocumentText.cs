using System.Text;

namespace OhMyStudy.Documents;

public sealed record DocumentTextRun(string Text, bool IsFormula);

/// <summary>Shared literal/math boundaries and wrapping for native reading and fixed PDF templates.</summary>
public static class DocumentText
{
    public static IReadOnlyList<DocumentTextRun> Split(string text)
    {
        var runs = new List<DocumentTextRun>();
        int plain = 0;
        for (int index = 0; index < text.Length; index++)
        {
            if (index > 0 && text[index - 1] == '\\') continue;
            string? opening = text.AsSpan(index).StartsWith("$$") ? "$$"
                : text.AsSpan(index).StartsWith("\\(") ? "\\("
                : text.AsSpan(index).StartsWith("\\[") ? "\\["
                : text[index] == '$' ? "$" : null;
            if (opening is null) continue;
            string closing = opening switch { "\\(" => "\\)", "\\[" => "\\]", _ => opening };
            int end = text.IndexOf(closing, index + opening.Length, StringComparison.Ordinal);
            if (end < 0 || end == index + opening.Length) continue;
            if (index > plain) runs.Add(new(text[plain..index], false));
            runs.Add(new(text[(index + opening.Length)..end], true));
            index = end + closing.Length - 1;
            plain = index + 1;
        }
        if (plain < text.Length) runs.Add(new(text[plain..], false));
        if (runs.Count == 0) runs.Add(new(text, false));
        return runs;
    }

    // Add break opportunities, never remove source characters, including unsupported/long LaTeX.
    public static string AllowWrap(string text)
    {
        var result = new StringBuilder(text.Length);
        int length = 0;
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.IsWhiteSpace(rune)) length = 0;
            else if (++length > 32) { result.Append('\u200b'); length = 1; }
            result.Append(rune.ToString());
        }
        return result.ToString();
    }
}
