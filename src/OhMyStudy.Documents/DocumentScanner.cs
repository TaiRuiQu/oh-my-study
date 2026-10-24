namespace OhMyStudy.Documents;

public readonly record struct ScanPoint(double X, double Y);
public sealed record ScanDetection(ScanPoint[] Corners, bool Detected);
public sealed record ScanRaster(byte[] Bgra32, int Width, int Height);

public static class DocumentScanner
{
    public static ScanPoint[] FullFrame => [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];

    public static ScanDetection Detect(byte[] pixels, int width, int height, CancellationToken cancellationToken = default)
    {
        ValidateRaster(pixels, width, height);
        double scale = Math.Min(1, 640d / Math.Max(width, height));
        int w = Math.Max(2, (int)(width * scale)), h = Math.Max(2, (int)(height * scale));
        var gray = new byte[w * h];
        var histogram = new int[256];
        for (int y = 0; y < h; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < w; x++)
            {
                int i = ((int)(y * (height - 1d) / (h - 1)) * width + (int)(x * (width - 1d) / (w - 1))) * 4;
                byte value = (byte)((pixels[i] * 29 + pixels[i + 1] * 150 + pixels[i + 2] * 77) >> 8);
                gray[y * w + x] = value;
                histogram[value]++;
            }
        }
        double sum = 0, bestVariance = 0, lowSum = 0;
        int threshold = 127, lowCount = 0;
        for (int i = 0; i < 256; i++) sum += i * histogram[i];
        for (int i = 0; i < 255; i++)
        {
            lowCount += histogram[i]; lowSum += i * histogram[i];
            int highCount = gray.Length - lowCount;
            if (lowCount == 0 || highCount == 0) continue;
            double difference = lowSum / lowCount - (sum - lowSum) / highCount;
            double variance = (double)lowCount * highCount * difference * difference;
            if (variance > bestVariance) { bestVariance = variance; threshold = i; }
        }
        var mask = new bool[gray.Length];
        for (int i = 0; i < gray.Length; i++) mask[i] = gray[i] > threshold;
        ScanPoint[]? best = FindQuadrilateral(mask, w, h, cancellationToken);
        if (best is null)
        {
            Array.Clear(mask);
            for (int y = 1; y < h - 1; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 1; x < w - 1; x++)
                {
                    int p = y * w + x;
                    int gx = -gray[p - w - 1] + gray[p - w + 1] - 2 * gray[p - 1] + 2 * gray[p + 1] - gray[p + w - 1] + gray[p + w + 1];
                    int gy = -gray[p - w - 1] - 2 * gray[p - w] - gray[p - w + 1] + gray[p + w - 1] + 2 * gray[p + w] + gray[p + w + 1];
                    if (Math.Abs(gx) + Math.Abs(gy) < 160) continue;
                    for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) mask[p + dy * w + dx] = true;
                }
            }
            best = FindQuadrilateral(mask, w, h, cancellationToken);
        }
        return best is null ? new(FullFrame, false) : new(best.Select(p => new ScanPoint(p.X / (w - 1), p.Y / (h - 1))).ToArray(), true);
    }

    private static ScanPoint[]? FindQuadrilateral(bool[] mask, int w, int h, CancellationToken cancellationToken)
    {
        var queue = new int[mask.Length];
        ScanPoint[]? best = null;
        double largestArea = w * h * 0.15;
        for (int start = 0; start < mask.Length; start++)
        {
            if (!mask[start]) continue;
            int read = 0, write = 1;
            queue[0] = start; mask[start] = false;
            var boundary = new List<ScanPoint>();
            while (read < write)
            {
                if ((read & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                int p = queue[read++], x = p % w, y = p / w;
                bool edge = false;
                foreach ((int dx, int dy) in Neighbors)
                {
                    int nx = x + dx, ny = y + dy;
                    if (nx < 0 || nx >= w || ny < 0 || ny >= h) { edge = true; continue; }
                    int next = ny * w + nx;
                    if (!mask[next]) { edge = true; continue; }
                    mask[next] = false; queue[write++] = next;
                }
                if (edge) boundary.Add(new(x, y));
            }
            if (write < w + h || boundary.Count < 4) continue;
            var hull = Hull(boundary);
            double hullArea = Area(hull);
            if (hullArea <= largestArea || hullArea >= (w - 1d) * (h - 1) * 0.98) continue;
            while (hull.Count > 4)
            {
                int remove = 0;
                double smallest = double.MaxValue;
                for (int i = 0; i < hull.Count; i++)
                {
                    double triangle = Math.Abs(Cross(hull[(i + hull.Count - 1) % hull.Count], hull[i], hull[(i + 1) % hull.Count]));
                    if (triangle < smallest) { smallest = triangle; remove = i; }
                }
                hull.RemoveAt(remove);
            }
            double area = Area(hull);
            if (hull.Count != 4 || area < hullArea * 0.88 || area <= largestArea) continue;
            int first = Enumerable.Range(0, 4).MinBy(i => hull[i].X + hull[i].Y);
            var ordered = Enumerable.Range(0, 4).Select(i => hull[(i + first) % 4]).ToArray();
            if (Enumerable.Range(0, 4).Any(i => Distance(ordered[i], ordered[(i + 1) % 4]) < Math.Min(w, h) * 0.15)) continue;
            best = ordered; largestArea = area;
        }
        return best;
    }

    private static readonly (int X, int Y)[] Neighbors = [(1, 0), (-1, 0), (0, 1), (0, -1)];

    private static List<ScanPoint> Hull(List<ScanPoint> points)
    {
        var sorted = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        var hull = new List<ScanPoint>();
        foreach (var p in sorted)
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        int lower = hull.Count;
        for (int i = sorted.Length - 2; i >= 0; i--)
        {
            var p = sorted[i];
            while (hull.Count > lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        if (hull.Count > 1) hull.RemoveAt(hull.Count - 1);
        return hull;
    }

    public static ScanRaster Process(byte[] pixels, int width, int height, ScanPoint[] corners, string enhancement, CancellationToken cancellationToken = default, int maximumDimension = 3600)
    {
        ValidateRaster(pixels, width, height);
        ValidateCorners(corners);
        if (enhancement is not ("original" or "color" or "gray")) throw new InvalidDataException("Unknown photo enhancement mode");
        var p = corners.Select(point => new ScanPoint(point.X * (width - 1), point.Y * (height - 1))).ToArray();
        double targetWidth = Math.Max(Distance(p[0], p[1]), Distance(p[3], p[2]));
        double targetHeight = Math.Max(Distance(p[0], p[3]), Distance(p[1], p[2]));
        double scale = Math.Min(1, maximumDimension / Math.Max(targetWidth, targetHeight));
        int w = Math.Max(2, (int)Math.Round(targetWidth * scale)), h = Math.Max(2, (int)Math.Round(targetHeight * scale));
        double dx1 = p[1].X - p[2].X, dx2 = p[3].X - p[2].X, sx = p[0].X - p[1].X + p[2].X - p[3].X;
        double dy1 = p[1].Y - p[2].Y, dy2 = p[3].Y - p[2].Y, sy = p[0].Y - p[1].Y + p[2].Y - p[3].Y;
        double denominator = dx1 * dy2 - dx2 * dy1;
        double g = (sx * dy2 - dx2 * sy) / denominator, j = (dx1 * sy - sx * dy1) / denominator;
        double a = p[1].X - p[0].X + g * p[1].X, b = p[3].X - p[0].X + j * p[3].X;
        double d = p[1].Y - p[0].Y + g * p[1].Y, e = p[3].Y - p[0].Y + j * p[3].Y;
        var output = new byte[checked(w * h * 4)];
        for (int y = 0; y < h; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double v = y / (h - 1d);
            for (int x = 0; x < w; x++)
            {
                double u = x / (w - 1d), z = g * u + j * v + 1;
                double px = Math.Clamp((a * u + b * v + p[0].X) / z, 0, width - 1d);
                double py = Math.Clamp((d * u + e * v + p[0].Y) / z, 0, height - 1d);
                int x0 = (int)px, y0 = (int)py, x1 = Math.Min(x0 + 1, width - 1), y1 = Math.Min(y0 + 1, height - 1);
                double fx = px - x0, fy = py - y0;
                int offset = (y * w + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    double top = pixels[(y0 * width + x0) * 4 + c] * (1 - fx) + pixels[(y0 * width + x1) * 4 + c] * fx;
                    double bottom = pixels[(y1 * width + x0) * 4 + c] * (1 - fx) + pixels[(y1 * width + x1) * 4 + c] * fx;
                    output[offset + c] = (byte)Math.Round(top * (1 - fy) + bottom * fy);
                }
                output[offset + 3] = 255;
            }
        }
        if (enhancement != "original") return ClassicalDocumentRestoration.Restore(new(output, w, h), enhancement == "gray", cancellationToken);
        return new(output, w, h);
    }

    public static void ValidateCorners(ScanPoint[] points)
    {
        if (points.Length != 4 || points.Any(p => !double.IsFinite(p.X) || !double.IsFinite(p.Y) || p.X < 0 || p.X > 1 || p.Y < 0 || p.Y > 1))
            throw new InvalidDataException("Select four corners inside the photo");
        if (Area(points) < 0.01 || Enumerable.Range(0, 4).Any(i => Cross(points[i], points[(i + 1) % 4], points[(i + 2) % 4]) < 0.001))
            throw new InvalidDataException("Photo corners must form a clockwise convex page");
    }

    private static void ValidateRaster(byte[] pixels, int width, int height)
    {
        if (width < 2 || height < 2 || (long)width * height > 40_000_000 || pixels.LongLength != (long)width * height * 4)
            throw new InvalidDataException("Invalid scan image dimensions");
    }

    private static double Cross(ScanPoint a, ScanPoint b, ScanPoint c) => (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
    private static double Distance(ScanPoint a, ScanPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
    private static double Area(IReadOnlyList<ScanPoint> points) => Math.Abs(Enumerable.Range(0, points.Count).Sum(i => points[i].X * points[(i + 1) % points.Count].Y - points[(i + 1) % points.Count].X * points[i].Y)) / 2;
}
