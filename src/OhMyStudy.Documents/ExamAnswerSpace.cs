using System.Text;

namespace OhMyStudy.Documents;

public static class ExamAnswerSpace
{
    public static double HeightMillimetres(string? answer, double? points, string questionType, bool hasOptions,
        double columnWidthMillimetres, double minimumMillimetres = 12)
    {
        if (!double.IsFinite(columnWidthMillimetres) || columnWidthMillimetres <= 0)
            throw new ArgumentOutOfRangeException(nameof(columnWidthMillimetres));
        if (!double.IsFinite(minimumMillimetres) || minimumMillimetres is < 0 or > 100)
            throw new ArgumentOutOfRangeException(nameof(minimumMillimetres));
        if (points is { } score && (!double.IsFinite(score) || score < 0))
            throw new ArgumentOutOfRangeException(nameof(points));
        string type = questionType.Replace("-", " ", StringComparison.Ordinal).Replace("_", " ", StringComparison.Ordinal);
        if (hasOptions || type.Contains("选择", StringComparison.Ordinal) || type.Contains("填空", StringComparison.Ordinal)
            || type.Contains("choice", StringComparison.OrdinalIgnoreCase) || type.Contains("fill", StringComparison.OrdinalIgnoreCase)
            || type.Contains("blank", StringComparison.OrdinalIgnoreCase)) return 0;
        double capacity = Math.Max(8, columnWidthMillimetres / 3.5);
        double lines = 0;
        foreach (string line in (answer ?? "").ReplaceLineEndings("\n").Split('\n'))
        {
            double units = 0;
            foreach (Rune rune in line.EnumerateRunes()) units += rune.Value > 255 ? 1 : 0.5;
            lines += Math.Max(1, Math.Ceiling(units / capacity));
        }
        double writingLines = Math.Max(2, Math.Max(Math.Ceiling(lines * 1.3), Math.Ceiling((points ?? 4) / 2)));
        return Math.Max(minimumMillimetres, Math.Min(98, writingLines * 7));
    }
}
