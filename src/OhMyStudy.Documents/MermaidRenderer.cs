using System.Globalization;
using System.Text;

namespace OhMyStudy.Documents;

public sealed record MermaidDisplayLabel(string Text, double X, double Y, double Width, double Height, double FontSize);
public sealed record MermaidDisplay(string Svg, double Width, double Height, IReadOnlyList<MermaidDisplayLabel> Labels);

public static class MermaidRenderer
{
    private const int MaximumCodeLength = 65_536;
    private const int MaximumNodes = 128;
    private const int MaximumEdges = 256;
    private const double FontSize = 13;
    private const double Margin = 24;
    private const double RankGap = 76;
    private const double NodeGap = 22;

    public static string RenderSvg(string code) => Render(code, true).Svg;

    public static MermaidDisplay RenderDisplay(string code) => Render(code, false);

    private static MermaidDisplay Render(string code, bool includeText)
    {
        if (string.IsNullOrWhiteSpace(code)) throw new InvalidDataException("Mermaid code is empty.");
        if (code.Length > MaximumCodeLength) throw new InvalidDataException($"Mermaid code exceeds {MaximumCodeLength} characters.");
        var statements = SplitStatements(code);
        if (statements.Count == 0) throw new InvalidDataException("Mermaid code has no flowchart declaration.");
        var header = statements[0].Text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (header.Length != 2 || header[0] is not ("graph" or "flowchart") || !TryDirection(header[1], out var direction))
            throw new InvalidDataException($"Mermaid line {statements[0].Line}: expected 'flowchart' or 'graph' followed by TD, LR, BT, or RL.");
        var parser = new Parser();
        foreach (var statement in statements.Skip(1)) parser.Parse(statement);
        if (parser.Nodes.Count == 0) throw new InvalidDataException("The Mermaid flowchart has no nodes.");
        return Layout(parser.Nodes, parser.Edges, direction, includeText);
    }

    private static bool TryDirection(string text, out Direction direction)
    {
        direction = text.ToUpperInvariant() switch
        {
            "TD" => Direction.Down,
            "TB" => Direction.Down,
            "LR" => Direction.Right,
            "BT" => Direction.Up,
            "RL" => Direction.Left,
            _ => (Direction)(-1)
        };
        return Enum.IsDefined(direction);
    }

    private static List<Statement> SplitStatements(string code)
    {
        var result = new List<Statement>();
        var lines = code.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Split('\n');
        for (int lineIndex = 0; lineIndex < lines.Length; lineIndex++)
        {
            string line = lines[lineIndex];
            if (lineIndex == 0) line = line.TrimStart('﻿');
            var statement = new StringBuilder();
            bool quoted = false, escaped = false;
            int depth = 0;
            void Flush()
            {
                string text = statement.ToString().Trim();
                if (text.Length > 0) result.Add(new(text, lineIndex + 1));
                statement.Clear();
            }
            for (int index = 0; index < line.Length; index++)
            {
                char value = line[index];
                if (!quoted && value == '%' && index + 1 < line.Length && line[index + 1] == '%') break;
                if (!quoted && value == ';' && depth == 0) { Flush(); continue; }
                statement.Append(value);
                if (escaped) { escaped = false; continue; }
                if (quoted && value == '\\') { escaped = true; continue; }
                if (value == '"') { quoted = !quoted; continue; }
                if (quoted) continue;
                if (value is '[' or '(' or '{') depth++;
                else if (value is ']' or ')' or '}') depth--;
                if (depth < 0) throw new InvalidDataException($"Mermaid line {lineIndex + 1}: unexpected closing delimiter.");
            }
            if (depth != 0) throw new InvalidDataException($"Mermaid line {lineIndex + 1}: multiline or unclosed node labels are not supported.");
            Flush();
        }
        return result;
    }

    private static MermaidDisplay Layout(List<Node> nodes, List<Edge> edges, Direction direction, bool includeText)
    {
        var adjacency = nodes.Select(_ => new List<int>()).ToArray();
        foreach (var edge in edges) if (edge.Source != edge.Target) adjacency[edge.Source].Add(edge.Target);
        var states = new int[nodes.Count];
        void Visit(int index)
        {
            if (states[index] == 2) return;
            states[index] = 1;
            foreach (int target in adjacency[index])
            {
                if (states[target] == 1) continue;
                Visit(target);
                nodes[index].Rank = Math.Max(nodes[index].Rank, nodes[target].Rank + 1);
            }
            states[index] = 2;
        }
        for (int index = 0; index < nodes.Count; index++) Visit(index);
        int maximumRank = nodes.Max(node => node.Rank);
        foreach (var node in nodes) node.Rank = maximumRank - node.Rank;
        bool vertical = direction is Direction.Down or Direction.Up;
        int layers = maximumRank + 1;
        var groups = Enumerable.Range(0, layers).Select(layer => nodes.Where(node => node.Rank == layer).ToList()).ToArray();
        double width, height;
        if (vertical)
        {
            width = groups.Max(group => group.Sum(node => node.Width) + NodeGap * (group.Count - 1)) + Margin * 2;
            height = groups.Sum(group => group.Max(node => node.Height)) + RankGap * (layers - 1) + Margin * 2;
            double y = Margin;
            foreach (var group in groups)
            {
                double x = (width - group.Sum(node => node.Width) - NodeGap * (group.Count - 1)) / 2;
                double layerHeight = group.Max(node => node.Height);
                foreach (var node in group)
                {
                    node.X = x;
                    node.Y = y + (layerHeight - node.Height) / 2;
                    x += node.Width + NodeGap;
                }
                y += layerHeight + RankGap;
            }
        }
        else
        {
            width = groups.Sum(group => group.Max(node => node.Width)) + RankGap * (layers - 1) + Margin * 2;
            height = groups.Max(group => group.Sum(node => node.Height) + NodeGap * (group.Count - 1)) + Margin * 2;
            double x = Margin;
            foreach (var group in groups)
            {
                double y = (height - group.Sum(node => node.Height) - NodeGap * (group.Count - 1)) / 2;
                double layerWidth = group.Max(node => node.Width);
                foreach (var node in group)
                {
                    node.X = x + (layerWidth - node.Width) / 2;
                    node.Y = y;
                    y += node.Height + NodeGap;
                }
                x += layerWidth + RankGap;
            }
        }
        if (direction == Direction.Left)
            foreach (var node in nodes) node.X = width - node.X - node.Width;
        else if (direction == Direction.Up)
            foreach (var node in nodes) node.Y = height - node.Y - node.Height;
        if (!double.IsFinite(width) || !double.IsFinite(height) || width > 100_000 || height > 100_000)
            throw new InvalidDataException("The Mermaid layout exceeds the supported dimensions.");
        var svg = new StringBuilder("<svg xmlns=\"http://www.w3.org/2000/svg\" width=\"" + N(width) + "px\" height=\"" + N(height)
            + "px\" viewBox=\"0 0 " + N(width) + " " + N(height) + "\" role=\"img\">");
        svg.Append("<rect width=\"100%\" height=\"100%\" fill=\"white\"/>");
        var labels = new List<MermaidDisplayLabel>();
        foreach (var edge in edges) AppendEdge(svg, edge, nodes[edge.Source], nodes[edge.Target], includeText, labels);
        foreach (var node in nodes) AppendNode(svg, node, includeText, labels);
        svg.Append("</svg>");
        return new(svg.ToString(), width, height, labels);
    }

    private static void AppendEdge(StringBuilder svg, Edge edge, Node source, Node target, bool includeText, List<MermaidDisplayLabel> labels)
    {
        svg.Append("<g stroke=\"#555\" stroke-width=\"1.5\" fill=\"none\">");
        double labelX, labelY, arrowX = 0, arrowY = 0, arrowFromX = 0, arrowFromY = 0;
        if (source == target)
        {
            double x = source.X + source.Width * 0.7;
            double y = source.Y;
            arrowX = source.X + source.Width * 0.3;
            arrowY = y;
            arrowFromX = x - 45;
            arrowFromY = y - 50;
            svg.Append("<path d=\"M ").Append(N(x)).Append(' ').Append(N(y)).Append(" C ")
                .Append(N(x + 45)).Append(' ').Append(N(y - 50)).Append(' ').Append(N(arrowFromX)).Append(' ').Append(N(arrowFromY)).Append(' ')
                .Append(N(arrowX)).Append(' ').Append(N(arrowY)).Append("\"/>");
            labelX = x;
            labelY = y - 45;
        }
        else
        {
            double sx = source.X + source.Width / 2, sy = source.Y + source.Height / 2;
            double tx = target.X + target.Width / 2, ty = target.Y + target.Height / 2;
            Boundary(source, tx - sx, ty - sy, out double startX, out double startY);
            Boundary(target, sx - tx, sy - ty, out double endX, out double endY);
            startX += sx; startY += sy; endX += tx; endY += ty;
            svg.Append("<line x1=\"").Append(N(startX)).Append("\" y1=\"").Append(N(startY))
                .Append("\" x2=\"").Append(N(endX)).Append("\" y2=\"").Append(N(endY)).Append("\"/>");
            arrowX = endX;
            arrowY = endY;
            arrowFromX = startX;
            arrowFromY = startY;
            labelX = (startX + endX) / 2;
            labelY = (startY + endY) / 2 - 5;
        }
        svg.Append("</g>");
        if (edge.Directed) AppendArrow(svg, arrowX, arrowY, arrowFromX, arrowFromY);
        if (edge.Label.Length > 0)
        {
            var lines = Wrap(edge.Label, 48);
            double labelWidth = lines.Max(Measure) + 8;
            double labelHeight = lines.Count * 17 + 4;
            svg.Append("<rect x=\"").Append(N(labelX - labelWidth / 2)).Append("\" y=\"").Append(N(labelY - labelHeight / 2))
                .Append("\" width=\"").Append(N(labelWidth)).Append("\" height=\"").Append(N(labelHeight)).Append("\" fill=\"white\" stroke=\"none\"/>");
            labels.Add(new(string.Join("\n", lines), labelX, labelY, labelWidth, labelHeight, 12));
            if (includeText) AppendText(svg, lines, labelX, labelY - (lines.Count - 1) * 8.5, 12);
        }
    }

    private static void AppendArrow(StringBuilder svg, double tipX, double tipY, double fromX, double fromY)
    {
        double dx = tipX - fromX, dy = tipY - fromY;
        double length = Math.Sqrt(dx * dx + dy * dy);
        if (length < 0.0001) return;
        double ux = dx / length, uy = dy / length;
        double baseX = tipX - ux * 9, baseY = tipY - uy * 9;
        double px = -uy * 4, py = ux * 4;
        svg.Append("<polygon points=\"").Append(N(tipX)).Append(',').Append(N(tipY)).Append(' ')
            .Append(N(baseX + px)).Append(',').Append(N(baseY + py)).Append(' ')
            .Append(N(baseX - px)).Append(',').Append(N(baseY - py)).Append("\" fill=\"#444\" stroke=\"none\"/>");
    }

    private static void Boundary(Node node, double dx, double dy, out double x, out double y)
    {
        if (Math.Abs(dx) < 0.0001 && Math.Abs(dy) < 0.0001) { x = 0; y = 0; return; }
        double halfWidth = node.Width / 2, halfHeight = node.Height / 2;
        double scale = node.Shape == NodeShape.Diamond
            ? halfWidth * halfHeight / (halfHeight * Math.Abs(dx) + halfWidth * Math.Abs(dy))
            : Math.Min(halfWidth / Math.Max(Math.Abs(dx), 0.0001), halfHeight / Math.Max(Math.Abs(dy), 0.0001));
        x = dx * scale;
        y = dy * scale;
    }

    private static void AppendNode(StringBuilder svg, Node node, bool includeText, List<MermaidDisplayLabel> labels)
    {
        svg.Append("<g stroke=\"#555\" stroke-width=\"1.4\" fill=\"#f7f9fc\">");
        if (node.Shape == NodeShape.Diamond)
            svg.Append("<polygon points=\"").Append(N(node.X + node.Width / 2)).Append(',').Append(N(node.Y)).Append(' ')
                .Append(N(node.X + node.Width)).Append(',').Append(N(node.Y + node.Height / 2)).Append(' ')
                .Append(N(node.X + node.Width / 2)).Append(',').Append(N(node.Y + node.Height)).Append(' ')
                .Append(N(node.X)).Append(',').Append(N(node.Y + node.Height / 2)).Append("\"/>");
        else
            svg.Append("<rect x=\"").Append(N(node.X)).Append("\" y=\"").Append(N(node.Y)).Append("\" width=\"").Append(N(node.Width))
                .Append("\" height=\"").Append(N(node.Height)).Append("\" rx=\"").Append(node.Shape == NodeShape.Stadium ? N(node.Height / 2) : "3").Append("\"/>");
        svg.Append("</g>");
        var lines = Wrap(node.Label, 36);
        double x = node.X + node.Width / 2, y = node.Y + node.Height / 2;
        labels.Add(new(string.Join("\n", lines), x, y, lines.Max(Measure) + 4, lines.Count * 17, FontSize));
        if (includeText) AppendText(svg, lines, x, y - (lines.Count - 1) * 8.5, FontSize);
    }

    private static void AppendText(StringBuilder svg, IReadOnlyList<string> lines, double x, double y, double size)
    {
        svg.Append("<text x=\"").Append(N(x)).Append("\" y=\"").Append(N(y)).Append("\" text-anchor=\"middle\" dominant-baseline=\"middle\" font-family=\"NSimSun, SimSun, serif\" font-size=\"")
            .Append(N(size)).Append("\" fill=\"#202124\" stroke=\"none\">");
        for (int index = 0; index < lines.Count; index++)
        {
            if (index == 0) svg.Append(Escape(lines[index]));
            else svg.Append("<tspan x=\"").Append(N(x)).Append("\" dy=\"").Append(N(size + 4)).Append("\">").Append(Escape(lines[index])).Append("</tspan>");
        }
        svg.Append("</text>");
    }

    private static List<string> Wrap(string label, int maximumUnits)
    {
        string normalized = label.Replace("<br/>", "\n", StringComparison.OrdinalIgnoreCase).Replace("<br />", "\n", StringComparison.OrdinalIgnoreCase)
            .Replace("<br>", "\n", StringComparison.OrdinalIgnoreCase);
        var result = new List<string>();
        foreach (string sourceLine in normalized.Split('\n'))
        {
            if (sourceLine.Length == 0) { result.Add(""); continue; }
            var line = new StringBuilder();
            int units = 0;
            foreach (var rune in sourceLine.EnumerateRunes())
            {
                int width = IsWide(rune) ? 2 : 1;
                if (units > 0 && units + width > maximumUnits)
                {
                    result.Add(line.ToString());
                    line.Clear();
                    units = 0;
                }
                line.Append(rune.ToString());
                units += width;
            }
            result.Add(line.ToString());
        }
        return result;
    }

    private static double Measure(string text)
    {
        double width = 0;
        foreach (var rune in text.EnumerateRunes()) width += IsWide(rune) ? FontSize : 7;
        return width;
    }

    private static bool IsWide(Rune rune)
    {
        int value = rune.Value;
        return value is >= 0x1100 and <= 0x115f or >= 0x2e80 and <= 0xa4cf or >= 0xac00 and <= 0xd7a3
            or >= 0xf900 and <= 0xfaff or >= 0xfe30 and <= 0xfe6f or >= 0xff00 and <= 0xff60 or >= 0xffe0 and <= 0xffe6
            or >= 0x1f300 and <= 0x1faff or >= 0x20000 and <= 0x3fffd;
    }

    private static string Escape(string text)
    {
        var result = new StringBuilder(text.Length);
        foreach (char value in text)
            result.Append(value switch
            {
                '&' => "&amp;",
                '<' => "&lt;",
                '>' => "&gt;",
                '"' => "&quot;",
                '\'' => "&apos;",
                _ when char.IsControl(value) => " ",
                _ => value.ToString()
            });
        return result.ToString();
    }

    private static string N(double value) => value.ToString("0.##", CultureInfo.InvariantCulture);

    private enum Direction { Down, Right, Up, Left }
    private enum NodeShape { Rectangle, Stadium, Diamond }
    private sealed record Statement(string Text, int Line);
    private sealed class Node
    {
        internal required string Id { get; init; }
        internal required string Label { get; set; }
        internal NodeShape Shape { get; set; }
        internal int Rank { get; set; }
        internal double X { get; set; }
        internal double Y { get; set; }
        internal double Width { get; set; }
        internal double Height { get; set; }
    }
    private sealed record Edge(int Source, int Target, string Label, bool Directed);

    private sealed class Parser
    {
        private readonly Dictionary<string, int> indexes = new(StringComparer.Ordinal);
        internal List<Node> Nodes { get; } = [];
        internal List<Edge> Edges { get; } = [];

        internal void Parse(Statement statement)
        {
            int position = 0;
            var source = ParseNode(statement, ref position);
            while (true)
            {
                SkipSpace(statement.Text, ref position);
                if (position == statement.Text.Length) return;
                var link = ParseLink(statement, ref position);
                var target = ParseNode(statement, ref position);
                if (Edges.Count >= MaximumEdges) throw Error(statement, $"more than {MaximumEdges} edges");
                Edges.Add(new(source, target, link.Label, link.Directed));
                source = target;
            }
        }

        private int ParseNode(Statement statement, ref int position)
        {
            SkipSpace(statement.Text, ref position);
            int start = position;
            while (position < statement.Text.Length && (char.IsLetterOrDigit(statement.Text[position]) || statement.Text[position] == '_')) position++;
            if (position == start) throw Error(statement, $"expected a node identifier at character {position + 1}");
            if (position - start > 64) throw Error(statement, "a node identifier exceeds 64 characters");
            string id = statement.Text[start..position];
            if (!indexes.TryGetValue(id, out int index))
            {
                if (Nodes.Count >= MaximumNodes) throw Error(statement, $"more than {MaximumNodes} nodes");
                index = Nodes.Count;
                indexes.Add(id, index);
                var node = new Node { Id = id, Label = id };
                Nodes.Add(node);
                Size(node);
            }
            SkipSpace(statement.Text, ref position);
            if (position >= statement.Text.Length || statement.Text[position] is not ('[' or '(' or '{')) return index;
            char opening = statement.Text[position++];
            char closing = opening switch { '[' => ']', '(' => ')', _ => '}' };
            int labelStart = position;
            bool quoted = false, escaped = false;
            while (position < statement.Text.Length)
            {
                char value = statement.Text[position];
                if (escaped) escaped = false;
                else if (quoted && value == '\\') escaped = true;
                else if (value == '"') quoted = !quoted;
                else if (!quoted && value == closing) break;
                position++;
            }
            if (position >= statement.Text.Length || quoted) throw Error(statement, "an unclosed node label");
            string label = statement.Text[labelStart..position++].Trim();
            if (label.Length >= 2 && label[0] == '"' && label[^1] == '"') label = label[1..^1];
            if (label.Length > 512) throw Error(statement, "a node label exceeds 512 characters");
            var shape = opening switch { '[' => NodeShape.Rectangle, '(' => NodeShape.Stadium, _ => NodeShape.Diamond };
            Nodes[index].Label = label;
            Nodes[index].Shape = shape;
            Size(Nodes[index]);
            return index;
        }

        private static void Size(Node node)
        {
            var lines = Wrap(node.Label, 36);
            node.Width = Math.Max(node.Shape == NodeShape.Diamond ? 100 : 72, lines.Max(Measure) + (node.Shape == NodeShape.Diamond ? 48 : 28));
            node.Height = Math.Max(node.Shape == NodeShape.Diamond ? 64 : 42, lines.Count * 17 + (node.Shape == NodeShape.Diamond ? 30 : 18));
        }

        private static Link ParseLink(Statement statement, ref int position)
        {
            string text = statement.Text;
            bool directed;
            if (Consume(text, ref position, "-->")) directed = true;
            else if (Consume(text, ref position, "---")) directed = false;
            else if (Consume(text, ref position, "--"))
            {
                int arrow = text.IndexOf("-->", position, StringComparison.Ordinal);
                int line = text.IndexOf("---", position, StringComparison.Ordinal);
                int end = arrow < 0 ? line : line < 0 ? arrow : Math.Min(arrow, line);
                if (end < 0) throw Error(statement, "an incomplete link");
                directed = arrow >= 0 && end == arrow;
                string label = text[position..end].Trim();
                position = end + 3;
                return new(ValidateLabel(label, statement), directed);
            }
            else throw Error(statement, $"expected -->, ---, or -- text --> at character {position + 1}");
            SkipSpace(text, ref position);
            if (position < text.Length && text[position] == '|')
            {
                int end = text.IndexOf('|', position + 1);
                if (end < 0) throw Error(statement, "an unclosed pipe edge label");
                string label = text[(position + 1)..end];
                position = end + 1;
                return new(ValidateLabel(label, statement), directed);
            }
            return new("", directed);
        }

        private static string ValidateLabel(string label, Statement statement)
        {
            if (label.Length > 512) throw Error(statement, "an edge label exceeds 512 characters");
            return label;
        }

        private static bool Consume(string text, ref int position, string value)
        {
            if (!text.AsSpan(position).StartsWith(value, StringComparison.Ordinal)) return false;
            position += value.Length;
            return true;
        }

        private static void SkipSpace(string text, ref int position)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position])) position++;
        }

        private static InvalidDataException Error(Statement statement, string message) =>
            new($"Mermaid line {statement.Line}: {message}.");
    }

    private sealed record Link(string Label, bool Directed);
}
