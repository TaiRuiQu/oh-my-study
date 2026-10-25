using System.Text;

namespace OhMyStudy.Documents;

public enum DocumentFont { Default, Serif, SansSerif, Kai }

public sealed record DocumentTextRun(string Text, bool IsFormula, bool IsDisplay = false, DocumentFont Font = DocumentFont.Default);

/// <summary>Shared literal/math boundaries and wrapping for native reading and fixed PDF templates.</summary>
public static class DocumentText
{
    /// <summary>Recognize bare LaTeX in imported prose/options without changing stored content or guessing ordinary words.</summary>
    public static string NormalizeMath(string text)
    {
        string leading = text.TrimStart();
        bool option = leading.Length > 1 && leading[0] is >= 'A' and <= 'H' or >= 'a' and <= 'h'
            && leading[1] is '.' or ')' or '．' or '、' or '）';
        if (!text.Contains('\\') && !text.Contains('^') && !text.Contains('_') && !text.Contains('$') && !option) return text;
        var result = new StringBuilder(text.Length);
        foreach (var run in SplitMath(text))
        {
            if (run.IsFormula)
            {
                string delimiter = run.IsDisplay ? "$$" : "$";
                result.Append(delimiter).Append(NormalizeFormula(run.Text)).Append(delimiter);
                continue;
            }
            int start = 0;
            while (start < run.Text.Length)
            {
                if (!IsMathCharacter(run.Text[start])) { result.Append(run.Text[start++]); continue; }
                int end = start, depth = 0;
                while (end < run.Text.Length)
                {
                    char value = run.Text[end];
                    if (!IsMathCharacter(value) && depth == 0) break;
                    if (value == '{' && !IsEscaped(run.Text, end)) depth++;
                    if (value == '}' && !IsEscaped(run.Text, end)) depth = Math.Max(0, depth - 1);
                    end++;
                }
                string candidate = run.Text[start..end];
                int prefix = 0;
                while (prefix < candidate.Length && char.IsWhiteSpace(candidate[prefix])) prefix++;
                bool labeled = prefix + 1 < candidate.Length && candidate[prefix] is >= 'A' and <= 'H' or >= 'a' and <= 'h'
                    && candidate[prefix + 1] is '.' or ')' or '．' or '、' or '）';
                if (labeled) prefix += 2;
                while (prefix < candidate.Length && char.IsWhiteSpace(candidate[prefix])) prefix++;
                int tail = candidate.Length;
                while (tail > prefix && (char.IsWhiteSpace(candidate[tail - 1]) || candidate[tail - 1] is '.' or ',' or ';')) tail--;
                string math = candidate[prefix..tail];
                bool syntax = math.Contains('^') || math.Contains('_');
                syntax |= labeled && (math.Length == 1 && char.IsAsciiLetterOrDigit(math[0])
                    || math.Any(char.IsAsciiDigit) && math.All(value => !char.IsAsciiLetter(value))
                    || math.IndexOfAny(['=', '+', '-', '(', '[', '{']) >= 0);
                for (int index = 0; index + 1 < math.Length && !syntax; index++)
                    syntax = math[index] == '\\' && (char.IsAsciiLetter(math[index + 1]) || math[index + 1] is '{' or '}' or '|');
                // Imported identifiers such as ANSWER_ONLY_1 are prose, not implicit TeX.
                // Explicit delimiters and commands are never filtered by this heuristic.
                if (!math.Contains('\\') && System.Text.RegularExpressions.Regex.IsMatch(math, @"[A-Za-z]{2,}_|_[A-Za-z]{2,}")) syntax = false;
                result.Append(candidate.AsSpan(0, prefix));
                if (syntax) result.Append('$').Append(NormalizeFormula(math)).Append('$');
                else result.Append(math);
                result.Append(candidate.AsSpan(tail));
                start = end;
            }
        }
        return result.ToString();
    }

    public static string NormalizeFormula(string formula) => System.Text.RegularExpressions.Regex.Replace(formula, @"(?<!\\)_{2,}",
        static match => @"\underline{" + string.Concat(Enumerable.Repeat(@"\quad", Math.Min(match.Length, 12))) + "}");

    private static bool IsMathCharacter(char value) => char.IsAsciiLetterOrDigit(value) || value is ' ' or '\t'
        or '\\' or '{' or '}' or '(' or ')' or '[' or ']' or '^' or '_' or '=' or '+' or '-' or '*' or '/' or '<' or '>'
        or '|' or '!' or '.' or ',' or ';' or ':' or '%' or '$' or '\'' or '．' or '、' or '）'
        or >= '\u0370' and <= '\u03ff' or >= '\u2200' and <= '\u22ff';

    public static IReadOnlyList<DocumentTextRun> Split(string text) => ApplyFonts(SplitMath(text));

    private static IReadOnlyList<DocumentTextRun> SplitMath(string text)
    {
        var runs = new List<DocumentTextRun>();
        int plain = 0, backslashes = 0;
        HashSet<string>? unavailableClosings = null;
        for (int index = 0; index < text.Length; index++)
        {
            bool escaped = backslashes % 2 != 0;
            backslashes = text[index] == '\\' ? backslashes + 1 : 0;
            if (escaped) continue;
            string? opening = text.AsSpan(index).StartsWith("$$") ? "$$"
                : text.AsSpan(index).StartsWith("\\(") ? "\\("
                : text.AsSpan(index).StartsWith("\\[") ? "\\["
                : text[index] == '$' ? "$" : null;
            if (opening is null) continue;
            string closing = opening switch { "\\(" => "\\)", "\\[" => "\\]", _ => opening };
            if (unavailableClosings?.Contains(closing) == true) continue;
            int end = index + opening.Length;
            while ((end = text.IndexOf(closing, end, StringComparison.Ordinal)) >= 0)
            {
                if (!IsEscaped(text, end)) break;
                end += closing.Length;
            }
            if (end < 0) { (unavailableClosings ??= []).Add(closing); continue; }
            if (end == index + opening.Length) continue;
            if (index > plain) runs.Add(new(text[plain..index], false));
            runs.Add(new(text[(index + opening.Length)..end], true, opening is "$$" or "\\["));
            index = end + closing.Length - 1;
            plain = index + 1;
            backslashes = 0;
        }
        if (plain < text.Length) runs.Add(new(text[plain..], false));
        if (runs.Count == 0) runs.Add(new(text, false));
        return runs;
    }

    private static IReadOnlyList<DocumentTextRun> ApplyFonts(IReadOnlyList<DocumentTextRun> mathRuns)
    {
        var tokens = new List<FontToken>();
        foreach (var run in mathRuns)
        {
            if (run.IsFormula)
            {
                tokens.Add(new(run.Text, FontTokenKind.Formula, run.IsDisplay, DocumentFont.Default));
                continue;
            }
            int plain = 0;
            for (int index = 0; index < run.Text.Length;)
            {
                if (!TryFontDirective(run.Text, index, out var kind, out var font, out int length)) { index++; continue; }
                if (index > plain) tokens.Add(new(run.Text[plain..index], FontTokenKind.Text, false, DocumentFont.Default));
                tokens.Add(new(run.Text.Substring(index, length), kind, false, font));
                index += length;
                plain = index;
            }
            if (plain < run.Text.Length) tokens.Add(new(run.Text[plain..], FontTokenKind.Text, false, DocumentFont.Default));
        }
        if (tokens.Count == 0) return [new("", false)];
        var matched = new bool[tokens.Count];
        var openings = new Stack<int>();
        for (int index = 0; index < tokens.Count; index++)
        {
            if (tokens[index].Kind == FontTokenKind.Open) openings.Push(index);
            else if (tokens[index].Kind == FontTokenKind.Close && openings.Count > 0)
            {
                matched[index] = true;
                matched[openings.Pop()] = true;
            }
        }
        var runs = new List<DocumentTextRun>();
        var fonts = new Stack<DocumentFont>();
        var current = DocumentFont.Default;
        void Add(string text, bool formula, bool display)
        {
            if (!formula && runs.Count > 0 && runs[^1] is { IsFormula: false } previous && previous.Font == current)
                runs[^1] = previous with { Text = previous.Text + text };
            else runs.Add(new(text, formula, display, current));
        }
        for (int index = 0; index < tokens.Count; index++)
        {
            var token = tokens[index];
            if (matched[index] && token.Kind == FontTokenKind.Open)
            {
                fonts.Push(current);
                current = token.Font;
            }
            else if (matched[index] && token.Kind == FontTokenKind.Close) current = fonts.Pop();
            else Add(token.Text, token.Kind == FontTokenKind.Formula, token.IsDisplay);
        }
        return runs;
    }

    private static bool TryFontDirective(string text, int index, out FontTokenKind kind, out DocumentFont font, out int length)
    {
        kind = FontTokenKind.Text;
        font = DocumentFont.Default;
        length = 0;
        var rest = text.AsSpan(index);
        if (rest.StartsWith("{font:sans-serif}", StringComparison.Ordinal))
        {
            kind = FontTokenKind.Open;
            font = DocumentFont.SansSerif;
            length = 17;
            return true;
        }
        if (rest.StartsWith("{font:sans}", StringComparison.Ordinal))
        {
            kind = FontTokenKind.Open;
            font = DocumentFont.SansSerif;
            length = 11;
            return true;
        }
        if (rest.StartsWith("{font:serif}", StringComparison.Ordinal))
        {
            kind = FontTokenKind.Open;
            font = DocumentFont.Serif;
            length = 12;
            return true;
        }
        if (rest.StartsWith("{font:kai}", StringComparison.Ordinal))
        {
            kind = FontTokenKind.Open;
            font = DocumentFont.Kai;
            length = 10;
            return true;
        }
        if (!rest.StartsWith("{/font}", StringComparison.Ordinal)) return false;
        kind = FontTokenKind.Close;
        length = 7;
        return true;
    }

    private enum FontTokenKind { Text, Formula, Open, Close }
    private readonly record struct FontToken(string Text, FontTokenKind Kind, bool IsDisplay, DocumentFont Font);

    private static bool IsEscaped(string text, int index)
    {
        int backslashes = 0;
        while (index > 0 && text[--index] == '\\') backslashes++;
        return backslashes % 2 != 0;
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
