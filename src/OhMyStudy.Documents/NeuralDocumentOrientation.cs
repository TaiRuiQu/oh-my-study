using Microsoft.ML.OnnxRuntime;

namespace OhMyStudy.Documents;

public sealed record DocumentOrientation(ushort Rotation, double Confidence);

public sealed class NeuralDocumentOrientation : IDisposable
{
    private readonly InferenceSession _session;

    public NeuralDocumentOrientation(string path)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        _session = new(path, options);
    }

    public DocumentOrientation Detect(ScanRaster raster, CancellationToken cancellationToken = default)
    {
        const int size = 224, plane = size * size;
        var data = new float[3 * plane];
        double crop = Math.Min(raster.Width, raster.Height) * size / 256d;
        double left = (raster.Width - crop) / 2, top = (raster.Height - crop) / 2;
        double[] means = [.485, .456, .406], deviations = [.229, .224, .225];
        for (int y = 0; y < size; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < size; x++)
            {
                double px = Math.Clamp(left + (x + .5) * crop / size - .5, 0, raster.Width - 1d);
                double py = Math.Clamp(top + (y + .5) * crop / size - .5, 0, raster.Height - 1d);
                int x0 = (int)px, y0 = (int)py, x1 = Math.Min(x0 + 1, raster.Width - 1), y1 = Math.Min(y0 + 1, raster.Height - 1);
                double fx = px - x0, fy = py - y0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int c = 2 - channel;
                    double upper = raster.Bgra32[(y0 * raster.Width + x0) * 4 + c] * (1 - fx) + raster.Bgra32[(y0 * raster.Width + x1) * 4 + c] * fx;
                    double lower = raster.Bgra32[(y1 * raster.Width + x0) * 4 + c] * (1 - fx) + raster.Bgra32[(y1 * raster.Width + x1) * 4 + c] * fx;
                    data[channel * plane + y * size + x] = (float)(((upper * (1 - fy) + lower * fy) / 255 - means[channel]) / deviations[channel]);
                }
            }
        }
        lock (_session)
        {
            using var input = OrtValue.CreateTensorValueFromMemory(data, [1, 3, size, size]);
            using var options = new RunOptions();
            using var registration = cancellationToken.Register(() => options.Terminate = true);
            using var outputs = _session.Run(options, _session.InputNames, [input], _session.OutputNames);
            cancellationToken.ThrowIfCancellationRequested();
            var probabilities = outputs[0].GetTensorDataAsSpan<float>();
            if (probabilities.Length != 4) throw new InvalidDataException("Document orientation model must return four probabilities");
            int index = 0;
            for (int i = 1; i < 4; i++) if (probabilities[i] > probabilities[index]) index = i;
            double confidence = probabilities[index];
            if (!double.IsFinite(confidence)) throw new InvalidDataException("Invalid document orientation confidence");
            return new(confidence >= .8 ? (ushort)((360 - index * 90) % 360) : (ushort)0, confidence);
        }
    }

    public void Dispose()
    {
        _session.Dispose();
    }
}
