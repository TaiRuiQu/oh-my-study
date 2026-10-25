namespace OhMyStudy.Documents;

public sealed record DocumentTableCell(string Text, int Row, int Column, int RowSpan, int ColumnSpan);

public static class DocumentTable
{
    public static IReadOnlyList<DocumentTableCell> Layout(IReadOnlyList<IReadOnlyList<string>> rows)
    {
        ArgumentNullException.ThrowIfNull(rows);
        if (rows.Count == 0) return [];
        int columns = rows.Max(row => row.Count);
        if (columns == 0) return [];
        if (rows.Count > 2000 || columns > 100 || (long)rows.Count * columns > 50_000)
            throw new ArgumentException("The table exceeds the limit.", nameof(rows));
        int count = rows.Count * columns;
        var parents = Enumerable.Range(0, count).ToArray();
        var active = new bool[count];
        var markers = new bool[count];
        for (int row = 0; row < rows.Count; row++)
        {
            ArgumentNullException.ThrowIfNull(rows[row]);
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                active[index] = column < rows[row].Count;
                if (!active[index]) continue;
                string text = rows[row][column] ?? throw new ArgumentException("A table cell cannot be null.", nameof(rows));
                markers[index] = text.Trim() is "^" or "<" or ">";
            }
        }
        int Root(int index)
        {
            while (parents[index] != index)
            {
                parents[index] = parents[parents[index]];
                index = parents[index];
            }
            return index;
        }
        void Union(int left, int right)
        {
            left = Root(left);
            right = Root(right);
            if (left != right) parents[right] = left;
        }
        for (int row = 0; row < rows.Count; row++)
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                if (!markers[index]) continue;
                string marker = rows[row][column].Trim();
                int targetRow = row + (marker == "^" ? -1 : 0);
                int targetColumn = column + (marker == "<" ? -1 : marker == ">" ? 1 : 0);
                if (targetRow < 0 || targetColumn < 0 || targetColumn >= columns || targetColumn >= rows[targetRow].Count)
                    throw new InvalidDataException($"The table merge marker at row {row + 1}, column {column + 1} has no target.");
                Union(index, targetRow * columns + targetColumn);
            }
        var groups = new Dictionary<int, List<int>>();
        for (int index = 0; index < count; index++)
            if (active[index])
            {
                int root = Root(index);
                if (!groups.TryGetValue(root, out var group)) groups.Add(root, group = []);
                group.Add(index);
            }
        var result = new List<DocumentTableCell>(count);
        var anchors = new Dictionary<int, DocumentTableCell>();
        foreach (var group in groups.Values)
        {
            int minRow = group.Min(index => index / columns);
            int maxRow = group.Max(index => index / columns);
            int minColumn = group.Min(index => index % columns);
            int maxColumn = group.Max(index => index % columns);
            var anchorsInGroup = group.Where(index => !markers[index]).ToArray();
            int expected = (maxRow - minRow + 1) * (maxColumn - minColumn + 1);
            if (anchorsInGroup.Length != 1 || group.Count != expected)
            {
                int first = group[0];
                throw new InvalidDataException($"The table merge at row {minRow + 1}, column {minColumn + 1} is ambiguous or non-rectangular near row {first / columns + 1}, column {first % columns + 1}.");
            }
            int anchor = anchorsInGroup[0];
            int anchorRow = anchor / columns;
            int anchorColumn = anchor % columns;
            var cell = new DocumentTableCell(rows[anchorRow][anchorColumn], minRow, minColumn, maxRow - minRow + 1, maxColumn - minColumn + 1);
            anchors.Add(anchor, cell);
        }
        for (int row = 0; row < rows.Count; row++)
            for (int column = 0; column < columns; column++)
            {
                int index = row * columns + column;
                if (active[index] && markers[index]) continue;
                result.Add(anchors.TryGetValue(index, out var anchor) ? anchor : new("", row, column, 1, 1));
            }
        return result;
    }
}
