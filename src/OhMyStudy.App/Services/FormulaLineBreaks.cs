namespace OhMyStudy.App.Services;

internal static class FormulaLineBreaks
{
    internal static IReadOnlyList<string> Split(string source)
    {
        if (new[] { @"\begin", @"\def", @"\newcommand", @"\color", @"\tag", "%" }.Any(source.Contains)) return (string[])[source];
        var result = new List<string>();
        int braces = 0, fences = 0, start = 0;
        bool bar = false;
        for (int index = 0; index < source.Length; index++)
        {
            int position = index;
            char character = source[index];
            bool boundary = character is '=' or '+' or '-' or '<' or '>' or '≤' or '≥' or '≠';
            if (character == '\\')
            {
                int end = index + 1;
                while (end < source.Length && char.IsAsciiLetter(source[end])) end++;
                if (end == index + 1)
                {
                    if (end < source.Length && source[end] == '{') fences++;
                    if (end < source.Length && source[end] == '}') fences--;
                    index = end;
                    continue;
                }
                string command = source[(index + 1)..end];
                if (command == "left") fences++;
                if (command == "right") fences--;
                boundary = command is "le" or "leq" or "ge" or "geq" or "ne" or "neq" or "approx" or "equiv" or "pm" or "mp" or "times" or "cdot";
                index = end - 1;
            }
            else if (character == '{') braces++;
            else if (character == '}') braces--;
            else if (braces == 0 && character is '(' or '[') fences++;
            else if (braces == 0 && character is ')' or ']') fences--;
            else if (braces == 0 && character == '|') bar = !bar;
            if (!boundary || braces != 0 || fences != 0 || bar || position == start) continue;
            int previous = position - 1;
            while (previous >= start && char.IsWhiteSpace(source[previous])) previous--;
            if (previous < start || source[previous] is '=' or '+' or '-' or '<' or '>' or '^' or '_') continue;
            result.Add(source[start..position]);
            start = position;
        }
        result.Add(source[start..]);
        return result;
    }
}
