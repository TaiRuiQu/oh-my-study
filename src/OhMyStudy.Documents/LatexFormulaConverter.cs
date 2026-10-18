using System.Text;

namespace OhMyStudy.Documents;

/// <summary>A deliberately bounded LaTeX/mhchem subset. Unsupported input stays visible.</summary>
public sealed record FormulaConversion(bool IsSupported, string? TypstMath, string Original, string? Diagnostic, int ErrorOffset);

public static class LatexFormulaConverter
{
    public const int MaximumInputLength = 8192;
    public const int MaximumNesting = 32;
    public const int MaximumTokens = 4096;

    public static FormulaConversion Convert(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (source.Length > MaximumInputLength)
            return new(false, null, source[..MaximumInputLength] + "…", "公式超过 8192 字符限制。", MaximumInputLength);
        try
        {
            var parser = new Parser(source);
            var math = parser.Parse();
            return new(true, math, source, null, -1);
        }
        catch (FormulaException error)
        {
            return new(false, null, source, error.Message, error.Offset);
        }
    }

    // Escape every untrusted string. No input is used as a Typst identifier, path or code.
    internal static string Quote(string text)
    {
        var result = new StringBuilder("\"");
        foreach (var character in text)
        {
            result.Append(character switch
            {
                '\\' => "\\\\",
                '"' => "\\\"",
                '\n' => "\\n",
                '\r' => "\\r",
                '\t' => "\\t",
                _ when char.IsControl(character) => " ",
                _ => character.ToString()
            });
        }
        return result.Append('"').ToString();
    }

    private sealed class FormulaException(string message, int offset) : Exception(message)
    {
        public int Offset { get; } = offset;
    }

    private sealed class Parser(string source)
    {
        private int position;
        private int nesting;
        private int tokens;

        public string Parse()
        {
            var result = Sequence(null);
            if (string.IsNullOrWhiteSpace(result)) Fail("公式为空。 ");
            return result;
        }

        private void Fail(string message) => throw new FormulaException(message, position);
        private void Tick()
        {
            if (++tokens > MaximumTokens) Fail("公式超过 4096 个语法单元限制。 ");
        }
        private void Enter()
        {
            if (++nesting > MaximumNesting) Fail("公式嵌套超过 32 层限制。 ");
        }
        private void Space()
        {
            while (position < source.Length && char.IsWhiteSpace(source[position])) position++;
        }

        private string Sequence(char? closing)
        {
            Enter();
            var items = new List<string>();
            while (true)
            {
                Space();
                if (position >= source.Length)
                {
                    if (closing.HasValue) Fail("分组未闭合。 ");
                    break;
                }
                if (closing == source[position]) { position++; break; }
                if (source[position] == '}') Fail("存在多余的右花括号。 ");
                Tick();
                var atom = Atom();
                string? subscript = null;
                string? superscript = null;
                while (true)
                {
                    Space();
                    if (position == source.Length || source[position] is not ('_' or '^')) break;
                    var kind = source[position++];
                    var script = Argument();
                    if (kind == '_')
                    {
                        if (subscript is not null) Fail("同一对象有重复下标。 ");
                        subscript = script;
                    }
                    else
                    {
                        if (superscript is not null) Fail("同一对象有重复上标。 ");
                        superscript = script;
                    }
                }
                if (subscript is not null || superscript is not null)
                    atom = "attach(" + atom + (subscript is null ? "" : ", b: " + subscript)
                        + (superscript is null ? "" : ", t: " + superscript) + ")";
                items.Add(atom);
            }
            nesting--;
            return string.Join(" ", items);
        }

        private string Argument()
        {
            Enter();
            try
            {
                Space();
                if (position == source.Length) { Fail("缺少公式参数。 "); return ""; }
                var result = source[position] == '{' ? GroupArgument() : Atom();
                if (string.IsNullOrWhiteSpace(result)) Fail("公式参数不能为空。 ");
                return result;
            }
            finally { nesting--; }
        }

        private string GroupArgument() { position++; return Sequence('}'); }

        private string RawGroup()
        {
            Space();
            if (position == source.Length || source[position++] != '{') { Fail("命令需要花括号参数。 "); return ""; }
            var start = position;
            var depth = 1;
            while (position < source.Length)
            {
                var character = source[position++];
                if (character == '{' && ++depth > MaximumNesting) Fail("参数嵌套过深。 ");
                if (character == '}' && --depth == 0) return source[start..(position - 1)];
            }
            Fail("命令参数未闭合。 ");
            return "";
        }

        private string Atom()
        {
            Space();
            if (position == source.Length) { Fail("缺少公式对象。 "); return ""; }
            var character = source[position++];
            if (character == '{')
            {
                var grouped = Sequence('}');
                if (string.IsNullOrWhiteSpace(grouped)) Fail("公式分组不能为空。 ");
                return "attach(" + grouped + ")";
            }
            if (character == '(') return "(" + Sequence(')') + ")";
            if (character == '[') return "\\[ " + Sequence(']') + " \\]";
            if (character is ')' or ']') Fail("公式定界符不匹配。 ");
            if (character == '\\') return Command();
            if (char.IsAsciiLetter(character)) return character.ToString();
            if (char.IsAsciiDigit(character))
            {
                var start = position - 1;
                while (position < source.Length && (char.IsAsciiDigit(source[position]) || source[position] == '.')) position++;
                return Quote(source[start..position]);
            }
            if (character is '+' or '-' or '=' or ',' or ';' or ':' or '!' or '/' or '|' or '<' or '>')
                return character switch { ';' or ':' or '!' or '/' or '|' => Quote(character.ToString()), _ => character.ToString() };
            if (character == '*') return "dot.op";
            if (character == '\'') return "prime";
            if (character > 127 && !char.IsControl(character)) return Quote(character.ToString());
            Fail("不支持此公式字符；请保留原式或改用受支持的语法。 ");
            return "";
        }

        private string Command()
        {
            var start = position;
            while (position < source.Length && char.IsAsciiLetter(source[position])) position++;
            var command = source[start..position];
            if (command.Length == 0)
            {
                if (position == source.Length) { Fail("公式以反斜线结尾。 "); return ""; }
                return source[position++] switch
                {
                    ',' or ';' or ' ' or '!' => "space",
                    '{' => "\\{",
                    '}' => "\\}",
                    '%' => Quote("%"),
                    _ => throw new FormulaException("不支持该转义命令。 ", position - 1)
                };
            }
            switch (command)
            {
                case "frac":
                case "dfrac":
                case "tfrac":
                    var numerator = Argument();
                    return "frac(" + numerator + ", " + Argument() + ")";
                case "sqrt":
                    Space();
                    if (position < source.Length && source[position] == '[')
                    {
                        position++;
                        var index = Sequence(']');
                        return "root(" + index + ", " + Argument() + ")";
                    }
                    return "sqrt(" + Argument() + ")";
                case "text":
                case "textrm":
                case "operatorname":
                    var text = RawGroup();
                    if (text.Contains('\\') || text.Contains('{') || text.Contains('}')) Fail("文字参数不支持嵌套命令。 ");
                    return Quote(text);
                case "mathrm": return "upright(" + Argument() + ")";
                case "mathbf": return "bold(" + Argument() + ")";
                case "mathit": return "italic(" + Argument() + ")";
                case "overline": return "overline(" + Argument() + ")";
                case "vec": return "arrow(" + Argument() + ")";
                case "left":
                case "right":
                    Space();
                    if (position < source.Length && source[position] == '.') { position++; return ""; }
                    if (position == source.Length || source[position] is not ('(' or ')' or '[' or ']' or '|')) Fail("不支持此伸缩定界符。 ");
                    return ""; // The sequence parser owns and validates the following delimiter.
                case "begin": return Matrix(RawGroup());
                case "ce": return Chemistry(RawGroup());
                case "quad": return "quad";
                case "qquad": return "wide";
            }
            var mapped = command switch
            {
                "sum" => "sum",
                "prod" => "product",
                "int" => "integral",
                "iint" => "integral.double",
                "infty" => "infinity",
                "cdot" => "dot.op",
                "times" => "times",
                "div" => "div",
                "pm" => "plus.minus",
                "mp" => "minus.plus",
                "le" or "leq" => "lt.eq",
                "ge" or "geq" => "gt.eq",
                "ne" or "neq" => "eq.not",
                "approx" => "approx",
                "equiv" => "equiv",
                "in" => "in",
                "notin" => "in.not",
                "subset" => "subset",
                "cup" => "union",
                "cap" => "inter",
                "to" or "rightarrow" => "arrow.r",
                "leftarrow" => "arrow.l",
                "leftrightarrow" => "arrow.l.r",
                "Rightarrow" => "arrow.r.double",
                "Leftrightarrow" => "arrow.l.r.double",
                "uparrow" => "arrow.t",
                "downarrow" => "arrow.b",
                "ldots" or "cdots" => "dots.h",
                "sin" => "sin",
                "cos" => "cos",
                "tan" => "tan",
                "ln" => "ln",
                "log" => "log",
                "lim" => "lim",
                "max" => "max",
                "min" => "min",
                "det" => "det",
                "alpha" => "alpha",
                "beta" => "beta",
                "gamma" => "gamma",
                "delta" => "delta",
                "epsilon" or "varepsilon" => "epsilon",
                "theta" => "theta",
                "lambda" => "lambda",
                "mu" => "mu",
                "nu" => "nu",
                "xi" => "xi",
                "pi" => "pi",
                "rho" => "rho",
                "sigma" => "sigma",
                "tau" => "tau",
                "phi" or "varphi" => "phi",
                "chi" => "chi",
                "psi" => "psi",
                "omega" => "omega",
                "Delta" => "Delta",
                "Sigma" => "Sigma",
                "Omega" => "Omega",
                "Gamma" => "Gamma",
                "Theta" => "Theta",
                "Phi" => "Phi",
                _ => null
            };
            if (mapped is null) Fail("不支持的 LaTeX 命令：\\" + command + "。 ");
            return mapped!;
        }

        private string Matrix(string environment)
        {
            if (environment is not ("matrix" or "pmatrix" or "bmatrix" or "vmatrix")) Fail("不支持此矩阵环境。 ");
            var terminator = "\\end{" + environment + "}";
            var end = source.IndexOf(terminator, position, StringComparison.Ordinal);
            if (end < 0) { Fail("矩阵环境未闭合。 "); return ""; }
            var contentStart = position;
            var content = source[position..end];
            position = end + terminator.Length;
            var rows = content.Split("\\\\", StringSplitOptions.None);
            if (rows.Length > 16) Fail("矩阵最多支持 16 行。 ");
            var width = -1;
            var parsedRows = new List<string>();
            var cellOffset = contentStart;
            foreach (var row in rows)
            {
                var columns = row.Split('&');
                if (columns.Length > 16 || (width >= 0 && width != columns.Length)) Fail("矩阵列数不一致或超过 16 列。 ");
                width = columns.Length;
                var parsedColumns = new List<string>();
                foreach (var cell in columns)
                {
                    // Reuse shared budgets and depth; nested matrix environments are explicitly unsupported.
                    if (cell.Contains("\\begin", StringComparison.Ordinal)) Fail("当前不支持嵌套矩阵。 ");
                    tokens += cell.Length;
                    if (tokens > MaximumTokens) Fail("矩阵超过语法单元限制。 ");
                    var parser = new Parser(cell) { nesting = nesting };
                    try { parsedColumns.Add(parser.Parse()); }
                    catch (FormulaException error) { throw new FormulaException(error.Message, cellOffset + error.Offset); }
                    cellOffset += cell.Length + 1;
                }
                parsedRows.Add(string.Join(", ", parsedColumns));
                cellOffset++; // Row separator is two backslashes; a column delimiter is one character.
            }
            var delimiter = environment switch { "pmatrix" => "(", "bmatrix" => "[", "vmatrix" => "|", _ => "" };
            return "mat(delim: " + (delimiter.Length == 0 ? "none" : Quote(delimiter)) + ", " + string.Join("; ", parsedRows) + ")";
        }

        private string Chemistry(string chemical)
        {
            var index = 0;
            var depth = 0;
            string ChemicalSequence(char? closing)
            {
                if (++depth + nesting > MaximumNesting) Fail("化学式嵌套过深。 ");
                var items = new List<string>();
                while (index < chemical.Length)
                {
                    Tick();
                    var current = chemical[index];
                    if (char.IsWhiteSpace(current)) { index++; continue; }
                    if (closing == current) { index++; depth--; return string.Join(" ", items); }
                    var rest = chemical.AsSpan(index);
                    string? arrow = null;
                    var arrowLength = 0;
                    foreach (var candidate in new[] { "<=>", "<->", "->", "<-", "=>" })
                    {
                        if (!rest.StartsWith(candidate, StringComparison.Ordinal)) continue;
                        arrow = candidate switch { "<=>" => "harpoons.ltrb", "<->" => "arrow.l.r.long", "<-" => "arrow.l.long", "=>" => "arrow.r.double.long", _ => "arrow.r.long" };
                        arrowLength = candidate.Length;
                        break;
                    }
                    if (arrow is not null)
                    {
                        index += arrowLength;
                        var labels = new List<string>();
                        while (index < chemical.Length && chemical[index] == '[' && labels.Count < 2)
                        {
                            var labelStart = ++index;
                            while (index < chemical.Length && chemical[index] != ']') index++;
                            if (index == chemical.Length) Fail("化学反应条件未闭合。 ");
                            var label = chemical[labelStart..index++];
                            if (label.StartsWith("\\text{", StringComparison.Ordinal) && label.EndsWith('}')) label = label[6..^1];
                            if (label.IndexOfAny(['\\', '{', '}', '[']) >= 0) Fail("化学条件当前仅支持普通文字或 \\text。 ");
                            labels.Add(Quote(label));
                        }
                        items.Add("attach(stretch(" + arrow + ")" + (labels.Count > 0 ? ", t: " + labels[0] : "") + (labels.Count > 1 ? ", b: " + labels[1] : "") + ")");
                        continue;
                    }
                    if (current == '+') { index++; items.Add("+"); continue; }
                    if (current == 'v' && (index + 1 == chemical.Length || char.IsWhiteSpace(chemical[index + 1]))) { index++; items.Add("arrow.b"); continue; }
                    string atom;
                    if (current == '(')
                    {
                        index++;
                        atom = "(" + ChemicalSequence(')') + ")";
                    }
                    else if (char.IsAsciiLetter(current))
                    {
                        var start = index++;
                        while (index < chemical.Length && char.IsAsciiLetterLower(chemical[index])) index++;
                        // Quoted adjacent strings add word spacing. Letter atoms keep chemical bonds tight.
                        atom = "upright(" + string.Join(" ", chemical[start..index].Select(letter => letter.ToString())) + ")";
                    }
                    else if (char.IsAsciiDigit(current))
                    {
                        var start = index++;
                        while (index < chemical.Length && (char.IsAsciiDigit(chemical[index]) || chemical[index] == '.')) index++;
                        items.Add(Quote(chemical[start..index]));
                        continue;
                    }
                    else { Fail("不支持的 mhchem 语法；当前支持元素、系数、上下标、离子及带文字条件的反应箭头。 "); return ""; }
                    if (index < chemical.Length && char.IsAsciiDigit(chemical[index]))
                    {
                        var start = index++;
                        while (index < chemical.Length && char.IsAsciiDigit(chemical[index])) index++;
                        atom = "attach(" + atom + ", b: " + Quote(chemical[start..index]) + ")";
                    }
                    if (index < chemical.Length && chemical[index] == '^')
                    {
                        index++;
                        var braces = index < chemical.Length && chemical[index] == '{';
                        if (braces) index++;
                        var start = index;
                        while (index < chemical.Length && (char.IsAsciiDigit(chemical[index]) || chemical[index] is '+' or '-')) index++;
                        if (index == start || (braces && (index == chemical.Length || chemical[index++] != '}'))) Fail("离子电荷参数无效。 ");
                        var charge = chemical[start..(braces ? index - 1 : index)];
                        atom = "attach(" + atom + ", t: " + Quote(charge) + ")";
                    }
                    items.Add(atom);
                }
                if (closing.HasValue) Fail("化学式括号未闭合。 ");
                depth--;
                return string.Join(" ", items);
            }
            return ChemicalSequence(null);
        }
    }
}
