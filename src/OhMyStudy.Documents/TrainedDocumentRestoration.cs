using Microsoft.ML.OnnxRuntime;

namespace OhMyStudy.Documents;

public sealed class TrainedDocumentRestoration : IDisposable
{
    private readonly InferenceSession _geometry;
    private readonly InferenceSession _illumination;

    public TrainedDocumentRestoration(string directory)
    {
        using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), InterOpNumThreads = 1 };
        _geometry = new(Path.Combine(directory, "geometry.onnx"), options);
        try { _illumination = new(Path.Combine(directory, "illumination.onnx"), options); }
        catch { _geometry.Dispose(); throw; }
    }

    public ScanRaster Restore(ScanRaster raster, bool rectify, bool appearance, CancellationToken cancellationToken = default, Action<string>? progress = null)
    {
        if (rectify)
        {
            progress?.Invoke("Correcting page geometry…");
            var view = Resize(raster, 488, 712, false, cancellationToken);
            for (int i = 0; i < view.Length; i++) view[i] = MathF.Round(view[i] * 255) / 255;
            float[] grid = Run(_geometry, view, 488, 712, [1, 2, 45, 31], cancellationToken);
            raster = Rectify(raster, grid, cancellationToken);
        }
        if (appearance)
        {
            progress?.Invoke("Enhancing white background…");
            var view = Resize(raster, 512, 512, true, cancellationToken);
            float[] illumination = Run(_illumination, view, 512, 512, [1, 3, 256, 256], cancellationToken);
            raster = Enhance(raster, illumination, cancellationToken);
        }
        return raster;
    }

    private static float[] Run(InferenceSession session, float[] rgb, int width, int height, long[] outputShape, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        int plane = width * height;
        float[] data = new float[rgb.Length];
        for (int i = 0; i < plane; i++)
            for (int c = 0; c < 3; c++) data[c * plane + i] = rgb[i * 3 + c];
        using var input = OrtValue.CreateTensorValueFromMemory(data, [1, 3, height, width]);
        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        try
        {
            using var outputs = session.Run(options, session.InputNames, [input], session.OutputNames);
            cancellationToken.ThrowIfCancellationRequested();
            if (!outputs[0].GetTensorTypeAndShape().Shape.SequenceEqual(outputShape))
                throw new InvalidDataException("The trained restoration model returned an unexpected tensor shape");
            return outputs[0].GetTensorDataAsSpan<float>().ToArray();
        }
        catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    private static ScanRaster Rectify(ScanRaster raster, float[] grid, CancellationToken cancellationToken)
    {
        const int gridWidth = 31, gridHeight = 45, plane = gridWidth * gridHeight;
        byte[] output = new byte[raster.Bgra32.Length];
        for (int y = 0; y < raster.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float gy = y * (gridHeight - 1f) / (raster.Height - 1);
            int y0 = (int)gy, y1 = Math.Min(y0 + 1, gridHeight - 1);
            float fy = gy - y0;
            for (int x = 0; x < raster.Width; x++)
            {
                float gx = x * (gridWidth - 1f) / (raster.Width - 1);
                int x0 = (int)gx, x1 = Math.Min(x0 + 1, gridWidth - 1);
                float fx = gx - x0;
                float nx = Interpolate(grid[y0 * gridWidth + x0], grid[y0 * gridWidth + x1], grid[y1 * gridWidth + x0], grid[y1 * gridWidth + x1], fx, fy);
                float ny = Interpolate(grid[plane + y0 * gridWidth + x0], grid[plane + y0 * gridWidth + x1], grid[plane + y1 * gridWidth + x0], grid[plane + y1 * gridWidth + x1], fx, fy);
                float px = (nx + 1) * .5f * (raster.Width - 1), py = (ny + 1) * .5f * (raster.Height - 1);
                int left = (int)MathF.Floor(px), top = (int)MathF.Floor(py), offset = (y * raster.Width + x) * 4;
                for (int c = 0; c < 3; c++)
                {
                    float value = Interpolate(Pixel(raster, left, top, c), Pixel(raster, left + 1, top, c), Pixel(raster, left, top + 1, c), Pixel(raster, left + 1, top + 1, c), px - left, py - top);
                    output[offset + c] = (byte)Math.Clamp(MathF.Round(value), 0, 255);
                }
                output[offset + 3] = 255;
            }
        }
        return new(output, raster.Width, raster.Height);
    }

    private static float Pixel(ScanRaster raster, int x, int y, int channel) => x >= 0 && x < raster.Width && y >= 0 && y < raster.Height ? raster.Bgra32[(y * raster.Width + x) * 4 + channel] : 255;

    private static ScanRaster Enhance(ScanRaster raster, float[] prediction, CancellationToken cancellationToken)
    {
        const int size = 256, plane = size * size;
        float[] view = Resize(raster, size, size, true, cancellationToken);
        float[] balance = WhiteBalance(view);
        float[] field = new float[prediction.Length];
        for (int i = 0; i < plane; i++)
        {
            float r = view[i * 3] / balance[0], g = view[i * 3 + 1] / balance[1], b = view[i * 3 + 2] / balance[2];
            float maximum = Math.Max(r, Math.Max(g, b)), minimum = Math.Min(r, Math.Min(g, b));
            float protect = Unit(((maximum - minimum) / Math.Max(maximum, .001f) - .055f) / .12f);
            float peak = Math.Max(.08f, Math.Max(prediction[i], Math.Max(prediction[plane + i], prediction[2 * plane + i])));
            for (int c = 0; c < 3; c++) field[i * 3 + c] = prediction[c * plane + i] * (1 - protect) + peak * balance[c] * protect;
        }
        byte[] output = new byte[raster.Bgra32.Length];
        for (int y = 0; y < raster.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float gy = Math.Clamp((y + .5f) * size / raster.Height - .5f, 0, size - 1);
            int y0 = (int)gy, y1 = Math.Min(y0 + 1, size - 1);
            for (int x = 0; x < raster.Width; x++)
            {
                float gx = Math.Clamp((x + .5f) * size / raster.Width - .5f, 0, size - 1);
                int x0 = (int)gx, x1 = Math.Min(x0 + 1, size - 1), offset = (y * raster.Width + x) * 4;
                float sr = raster.Bgra32[offset + 2] / 255f, sg = raster.Bgra32[offset + 1] / 255f, sb = raster.Bgra32[offset] / 255f;
                float r = Unit(sr / Math.Max(.08f, Field(field, x0, x1, y0, y1, gx - x0, gy - y0, 0)));
                float g = Unit(sg / Math.Max(.08f, Field(field, x0, x1, y0, y1, gx - x0, gy - y0, 1)));
                float b = Unit(sb / Math.Max(.08f, Field(field, x0, x1, y0, y1, gx - x0, gy - y0, 2)));
                float balancedMax = Math.Max(sr / balance[0], Math.Max(sg / balance[1], sb / balance[2]));
                float balancedMin = Math.Min(sr / balance[0], Math.Min(sg / balance[1], sb / balance[2]));
                float neutral = Unit((.26f - (balancedMax - balancedMin) / Math.Max(balancedMax, .001f)) / .18f) * Unit((.78f - balancedMax) / .25f);
                float luminance = .299f * r + .587f * g + .114f * b;
                r = r * (1 - neutral) + luminance * neutral;
                g = g * (1 - neutral) + luminance * neutral;
                b = b * (1 - neutral) + luminance * neutral;
                float maximum = Math.Max(r, Math.Max(g, b)), minimum = Math.Min(r, Math.Min(g, b));
                float white = Unit((minimum - .82f) / .16f) * Unit((.12f - (maximum - minimum) / Math.Max(maximum, .001f)) / .07f);
                white = white * white * (3 - 2 * white);
                output[offset] = Tone(b, white);
                output[offset + 1] = Tone(g, white);
                output[offset + 2] = Tone(r, white);
                output[offset + 3] = 255;
            }
        }
        return new(output, raster.Width, raster.Height);
    }

    private static float Field(float[] data, int x0, int x1, int y0, int y1, float fx, float fy, int c) => Interpolate(data[(y0 * 256 + x0) * 3 + c], data[(y0 * 256 + x1) * 3 + c], data[(y1 * 256 + x0) * 3 + c], data[(y1 * 256 + x1) * 3 + c], fx, fy);

    private static byte Tone(float value, float white) => (byte)Math.Clamp(MathF.Round((value * .3f + (Unit((value - .045f) / .945f) * (1 - white) + white) * .7f) * 255), 0, 255);

    private static float Unit(float value) => Math.Clamp(value, 0, 1);

    private static float Interpolate(float a, float b, float c, float d, float x, float y) => (a * (1 - x) + b * x) * (1 - y) + (c * (1 - x) + d * x) * y;

    private static float[] WhiteBalance(float[] rgb)
    {
        int count = rgb.Length / 3;
        float[] white = new float[3];
        for (int c = 0; c < 3; c++)
        {
            float[] channel = new float[count];
            for (int i = 0; i < count; i++) channel[i] = rgb[i * 3 + c];
            Array.Sort(channel);
            double position = (count - 1) * .94;
            int index = (int)position;
            white[c] = channel[index] + (channel[Math.Min(index + 1, count - 1)] - channel[index]) * (float)(position - index);
        }
        float peak = Math.Max(.08f, white.Max());
        for (int c = 0; c < 3; c++) white[c] = Math.Clamp(white[c] / peak, .5f, 1);
        return white;
    }

    private static float[] Resize(ScanRaster raster, int width, int height, bool area, CancellationToken cancellationToken)
    {
        float[] output = new float[width * height * 3];
        if (area)
        {
            var xs = Contributions(raster.Width, width);
            var ys = Contributions(raster.Height, height);
            float[] rows = new float[raster.Height * width * 3];
            for (int y = 0; y < raster.Height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                for (int x = 0; x < width; x++)
                    foreach (var (source, weight) in xs[x])
                        for (int c = 0; c < 3; c++) rows[(y * width + x) * 3 + c] += raster.Bgra32[(y * raster.Width + source) * 4 + 2 - c] * (weight / 255);
            }
            for (int y = 0; y < height; y++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                foreach (var (source, weight) in ys[y])
                    for (int x = 0; x < width * 3; x++) output[y * width * 3 + x] += rows[source * width * 3 + x] * weight;
            }
            return output;
        }
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float py = Math.Clamp((y + .5f) * raster.Height / height - .5f, 0, raster.Height - 1);
            int y0 = (int)py, y1 = Math.Min(y0 + 1, raster.Height - 1);
            for (int x = 0; x < width; x++)
            {
                float px = Math.Clamp((x + .5f) * raster.Width / width - .5f, 0, raster.Width - 1);
                int x0 = (int)px, x1 = Math.Min(x0 + 1, raster.Width - 1);
                for (int c = 0; c < 3; c++) output[(y * width + x) * 3 + c] = Interpolate(Pixel(raster, x0, y0, 2 - c), Pixel(raster, x1, y0, 2 - c), Pixel(raster, x0, y1, 2 - c), Pixel(raster, x1, y1, 2 - c), px - x0, py - y0) / 255;
            }
        }
        return output;
    }

    private static (int Index, float Weight)[][] Contributions(int source, int target)
    {
        var result = new (int, float)[target][];
        double scale = source / (double)target;
        for (int i = 0; i < target; i++)
        {
            double left = i * scale, right = (i + 1) * scale;
            var weights = new List<(int, float)>();
            for (int j = (int)Math.Floor(left); j < Math.Ceiling(right) && j < source; j++)
                weights.Add((j, (float)((Math.Min(right, j + 1) - Math.Max(left, j)) / scale)));
            result[i] = weights.ToArray();
        }
        return result;
    }

    public void Dispose()
    {
        _geometry.Dispose();
        _illumination.Dispose();
    }
}
