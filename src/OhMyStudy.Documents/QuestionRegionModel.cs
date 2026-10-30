using Microsoft.ML.OnnxRuntime;

namespace OhMyStudy.Documents;

public sealed record QuestionBox(double Left, double Top, double Right, double Bottom, double Confidence);

public sealed class QuestionRegionModel(string path) : IDisposable
{
    private readonly InferenceSession session = CreateSession(path);
    private static InferenceSession CreateSession(string path)
    {
        using var options = new SessionOptions { IntraOpNumThreads = 2, InterOpNumThreads = 1 };
        return new(path, options);
    }

    public List<QuestionBox> Detect(ScanRaster raster, CancellationToken cancellationToken)
    {
        var shape = session.InputMetadata[session.InputNames[0]].Dimensions;
        if (shape.Length != 4 || shape[0] != 1 || shape[1] != 3 || shape[2] != 1280 || shape[3] != 1280)
            throw new InvalidDataException("Question region ONNX models must use a fixed [1,3,1280,1280] RGB input.");
        const int size = 1280, plane = size * size;
        double scale = Math.Min(size / (double)raster.Width, size / (double)raster.Height);
        int width = (int)Math.Round(raster.Width * scale), height = (int)Math.Round(raster.Height * scale);
        int left = (size - width) / 2, top = (size - height) / 2;
        var data = new float[3 * plane];
        Array.Fill(data, 114f / 255);
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            double py = Math.Clamp((y + .5) * raster.Height / height - .5, 0, raster.Height - 1d);
            int y0 = (int)py, y1 = Math.Min(y0 + 1, raster.Height - 1);
            for (int x = 0; x < width; x++)
            {
                double px = Math.Clamp((x + .5) * raster.Width / width - .5, 0, raster.Width - 1d);
                int x0 = (int)px, x1 = Math.Min(x0 + 1, raster.Width - 1);
                double fx = px - x0, fy = py - y0;
                for (int channel = 0; channel < 3; channel++)
                {
                    int c = 2 - channel;
                    double upper = raster.Bgra32[(y0 * raster.Width + x0) * 4 + c] * (1 - fx) + raster.Bgra32[(y0 * raster.Width + x1) * 4 + c] * fx;
                    double lower = raster.Bgra32[(y1 * raster.Width + x0) * 4 + c] * (1 - fx) + raster.Bgra32[(y1 * raster.Width + x1) * 4 + c] * fx;
                    data[channel * plane + (y + top) * size + x + left] = (float)((upper * (1 - fy) + lower * fy) / 255);
                }
            }
        }
        using var input = OrtValue.CreateTensorValueFromMemory(data, [1, 3, size, size]);
        using var options = new RunOptions();
        using var registration = cancellationToken.Register(() => options.Terminate = true);
        using var output = session.Run(options, session.InputNames, [input], session.OutputNames);
        cancellationToken.ThrowIfCancellationRequested();
        var outputShape = output[0].GetTensorTypeAndShape().Shape;
        if (outputShape.Length != 3 || outputShape[0] != 1 || outputShape[2] != 6)
            throw new InvalidDataException("Export YOLO26 question models with nms=False. Expected end-to-end [1,N,6] boxes.");
        var values = output[0].GetTensorDataAsSpan<float>();
        var result = new List<QuestionBox>();
        for (int i = 0; i < values.Length; i += 6)
        {
            if (!float.IsFinite(values[i + 4]) || values[i + 4] < .2f || values[i + 5] != 0) continue;
            double x0 = Math.Clamp((values[i] - left) / width, 0, 1), y0 = Math.Clamp((values[i + 1] - top) / height, 0, 1);
            double x1 = Math.Clamp((values[i + 2] - left) / width, 0, 1), y1 = Math.Clamp((values[i + 3] - top) / height, 0, 1);
            if (x1 > x0 && y1 > y0) result.Add(new(x0, y0, x1, y1, values[i + 4]));
        }
        return result;
    }

    public void Dispose() => session.Dispose();
}
