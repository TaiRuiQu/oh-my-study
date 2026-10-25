using System.Globalization;

namespace OhMyStudy.App.Services;

internal readonly record struct WindowBounds(int Width, int Height, bool Maximized)
{
    internal static WindowBounds Default => new(1440, 960, false);

    internal static WindowBounds Parse(string text)
    {
        string[] fields = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        if (fields is ["max"]) return Default with { Maximized = true };
        if (fields.Length is not (2 or 3) || !int.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out int width)
            || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out int height) || width < 460 || height < 420
            || fields.Length == 3 && fields[2] is not ("0" or "1")) return Default;
        return new(width, height, fields.Length == 3 && fields[2] == "1");
    }

    public override string ToString() => string.Create(CultureInfo.InvariantCulture, $"{Width} {Height} {(Maximized ? 1 : 0)}");
}
