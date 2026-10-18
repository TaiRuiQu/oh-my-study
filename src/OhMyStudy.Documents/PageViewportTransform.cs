namespace OhMyStudy.Documents;

public readonly record struct PagePoint(double X, double Y);
public readonly record struct PageRect(double X, double Y, double Width, double Height);
public readonly record struct PagePixelRect(uint X, uint Y, uint Width, uint Height);

/// <summary>Maps unrotated source pixels to a clockwise-rotated, zoomed and translated viewport.</summary>
public sealed class PageViewportTransform
{
    public double SourceWidth { get; }
    public double SourceHeight { get; }
    public ushort Rotation { get; }
    public double Scale { get; }
    public PagePoint Offset { get; }
    public double RotatedWidth => Rotation is 90 or 270 ? SourceHeight : SourceWidth;
    public double RotatedHeight => Rotation is 90 or 270 ? SourceWidth : SourceHeight;

    public PageViewportTransform(double sourceWidth, double sourceHeight, ushort rotation, double scale = 1, double offsetX = 0, double offsetY = 0)
    {
        if (!double.IsFinite(sourceWidth) || !double.IsFinite(sourceHeight) || sourceWidth <= 0 || sourceHeight <= 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        ValidateRotation(rotation);
        if (!double.IsFinite(scale) || scale <= 0 || !double.IsFinite(offsetX) || !double.IsFinite(offsetY)) throw new ArgumentOutOfRangeException(nameof(scale));
        SourceWidth = sourceWidth; SourceHeight = sourceHeight; Rotation = rotation; Scale = scale; Offset = new(offsetX, offsetY);
    }

    public PagePoint SourceToViewport(PagePoint point)
    {
        var rotated = Rotation switch
        {
            0 => point,
            90 => new PagePoint(SourceHeight - point.Y, point.X),
            180 => new PagePoint(SourceWidth - point.X, SourceHeight - point.Y),
            270 => new PagePoint(point.Y, SourceWidth - point.X),
            _ => throw new InvalidOperationException()
        };
        return new(rotated.X * Scale + Offset.X, rotated.Y * Scale + Offset.Y);
    }

    public PagePoint ViewportToSource(PagePoint point)
    {
        double x = (point.X - Offset.X) / Scale, y = (point.Y - Offset.Y) / Scale;
        return Rotation switch
        {
            0 => new(x, y),
            90 => new(y, SourceHeight - x),
            180 => new(SourceWidth - x, SourceHeight - y),
            270 => new(SourceWidth - y, x),
            _ => throw new InvalidOperationException()
        };
    }

    public PagePoint SourceNormalizedToViewport(PagePoint normalized) => SourceToViewport(new(normalized.X * SourceWidth, normalized.Y * SourceHeight));
    public PagePoint ViewportToSourceNormalized(PagePoint point)
    {
        var source = ViewportToSource(point);
        return new(source.X / SourceWidth, source.Y / SourceHeight);
    }

    public PageRect SourceRegionToViewport(PageRect normalized)
    {
        ValidateRegion(normalized);
        PagePoint[] corners = [SourceNormalizedToViewport(new(normalized.X, normalized.Y)), SourceNormalizedToViewport(new(normalized.X + normalized.Width, normalized.Y)),
            SourceNormalizedToViewport(new(normalized.X, normalized.Y + normalized.Height)), SourceNormalizedToViewport(new(normalized.X + normalized.Width, normalized.Y + normalized.Height))];
        double left = corners.Min(p => p.X), top = corners.Min(p => p.Y);
        return new(left, top, corners.Max(p => p.X) - left, corners.Max(p => p.Y) - top);
    }

    public static PagePixelRect CropBounds(PageRect normalized, uint sourceWidth, uint sourceHeight)
    {
        ValidateRegion(normalized);
        if (sourceWidth == 0 || sourceHeight == 0) throw new ArgumentOutOfRangeException(nameof(sourceWidth));
        uint x = Math.Min(sourceWidth - 1, (uint)Math.Floor(normalized.X * sourceWidth));
        uint y = Math.Min(sourceHeight - 1, (uint)Math.Floor(normalized.Y * sourceHeight));
        uint width = Math.Max(1, Math.Min(sourceWidth - x, (uint)Math.Ceiling(normalized.Width * sourceWidth)));
        uint height = Math.Max(1, Math.Min(sourceHeight - y, (uint)Math.Ceiling(normalized.Height * sourceHeight)));
        return new(x, y, width, height);
    }

    public static void ValidateRotation(ushort rotation)
    {
        if (rotation is not (0 or 90 or 180 or 270)) throw new ArgumentOutOfRangeException(nameof(rotation), "只支持 0、90、180 或 270 度。");
    }

    public static void ValidateRegion(PageRect region)
    {
        if (!double.IsFinite(region.X) || !double.IsFinite(region.Y) || !double.IsFinite(region.Width) || !double.IsFinite(region.Height)
            || region.X < 0 || region.Y < 0 || region.X >= 1 || region.Y >= 1 || region.Width <= 0 || region.Height <= 0
            || region.X + region.Width > 1.000001 || region.Y + region.Height > 1.000001)
            throw new ArgumentOutOfRangeException(nameof(region), "来源区域超出原图。");
    }
}
