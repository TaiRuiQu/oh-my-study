using Microsoft.ML.OnnxRuntime;

namespace OhMyStudy.Documents;

public sealed class NeuralPageDetector : IDisposable
{
    private readonly InferenceSession _session;
    private readonly string _output;

    public NeuralPageDetector(string path)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        _session = new(path, options);
        _output = _session.OutputMetadata.First(pair => pair.Value.Dimensions.Length == 4 && pair.Value.Dimensions[1] == 4).Key;
    }

    public ScanDetection Detect(ScanRaster raster, CancellationToken cancellationToken = default)
    {
        const int size = 256, plane = size * size;
        var data = new float[3 * plane];
        for (int y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < size; x++)
            {
                double px = x * (raster.Width - 1d) / (size - 1), py = y * (raster.Height - 1d) / (size - 1);
                int x0 = (int)px, y0 = (int)py, x1 = Math.Min(x0 + 1, raster.Width - 1), y1 = Math.Min(y0 + 1, raster.Height - 1);
                double fx = px - x0, fy = py - y0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int c = 2 - channel;
                    double top = raster.Bgra32[(y0 * raster.Width + x0) * 4 + c] * (1 - fx) + raster.Bgra32[(y0 * raster.Width + x1) * 4 + c] * fx;
                    double bottom = raster.Bgra32[(y1 * raster.Width + x0) * 4 + c] * (1 - fx) + raster.Bgra32[(y1 * raster.Width + x1) * 4 + c] * fx;
                    data[channel * plane + y * size + x] = (float)((top * (1 - fy) + bottom * fy) / 255);
                }
            }
        }
        lock (_session)
        {
            using var input = OrtValue.CreateTensorValueFromMemory(data, [1, 3, size, size]);
            using var options = new RunOptions();
            using var registration = cancellationToken.Register(() => options.Terminate = true);
            using var outputs = _session.Run(options, _session.InputNames, [input], [_output]);
            cancellationToken.ThrowIfCancellationRequested();
            var tensor = outputs[0];
            var shape = tensor.GetTensorTypeAndShape().Shape;
            var values = tensor.GetTensorDataAsSpan<float>();
            int height = (int)shape[2], width = (int)shape[3], length = width * height;
            var points = new ScanPoint[4];
            for (int corner = 0; corner < 4; corner++)
            {
                int peak = 0;
                float confidence = float.NegativeInfinity;
                for (int i = 0; i < length; i++)
                    if (values[corner * length + i] > confidence) { confidence = values[corner * length + i]; peak = i; }
                if (!float.IsFinite(confidence) || confidence < .3) return new(DocumentScanner.FullFrame, false);
                int cx = peak % width, cy = peak / width;
                double total = 0, sumX = 0, sumY = 0;
                for (int y = Math.Max(0, cy - 4); y <= Math.Min(height - 1, cy + 4); y++)
                    for (int x = Math.Max(0, cx - 4); x <= Math.Min(width - 1, cx + 4); x++)
                    {
                        float value = values[corner * length + y * width + x];
                        if (value < confidence * .3) continue;
                        total += value; sumX += value * x; sumY += value * y;
                    }
                points[corner] = new(Math.Clamp(sumX / total / width, 0, 1), Math.Clamp(sumY / total / height, 0, 1));
            }
            var center = new ScanPoint(points.Average(p => p.X), points.Average(p => p.Y));
            points = points.Select(p => new ScanPoint(Math.Clamp(center.X + (p.X - center.X) * 1.025, 0, 1), Math.Clamp(center.Y + (p.Y - center.Y) * 1.025, 0, 1))).ToArray();
            points = points.OrderBy(p => Math.Atan2(p.Y - center.Y, p.X - center.X)).ToArray();
            int first = Enumerable.Range(0, 4).MinBy(i => points[i].X + points[i].Y);
            points = Enumerable.Range(0, 4).Select(i => points[(i + first) % 4]).ToArray();
            try { DocumentScanner.ValidateCorners(points); }
            catch (InvalidDataException) { return new(DocumentScanner.FullFrame, false); }
            return new(points, true);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
