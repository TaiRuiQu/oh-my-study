using System.Runtime.InteropServices;
using Microsoft.ML.OnnxRuntime;
using OpenCvSharp;

namespace OhMyStudy.Documents;

public sealed class NeuralDocumentRectifier : IDisposable
{
    private readonly InferenceSession _session;

    public NeuralDocumentRectifier(string path)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("DocScanner model is missing. Prepare the scanner assets before processing photos.", path);
        using var options = new SessionOptions { IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4), InterOpNumThreads = 1 };
        _session = new(path, options);
    }

    public ScanRaster Rectify(ScanRaster raster, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (raster.Width < 2 || raster.Height < 2 || (long)raster.Width * raster.Height > 40_000_000 || raster.Bgra32.LongLength != (long)raster.Width * raster.Height * 4)
            throw new InvalidDataException("Invalid rectification image dimensions");
        const int size = 288, plane = size * size;
        using var source = Mat.FromPixelData(raster.Height, raster.Width, MatType.CV_8UC4, raster.Bgra32);
        using var rgb = new Mat();
        using var floating = new Mat();
        using var resized = new Mat();
        Cv2.CvtColor(source, rgb, ColorConversionCodes.BGRA2RGB);
        rgb.ConvertTo(floating, MatType.CV_32FC3, 1d / 255);
        Cv2.Resize(floating, resized, new Size(size, size), 0, 0, InterpolationFlags.Linear);
        float[] bytes = new float[3 * plane];
        Marshal.Copy(resized.Data, bytes, 0, bytes.Length);
        float[] data = new float[bytes.Length];
        for (int i = 0; i < plane; i++)
            for (int c = 0; c < 3; c++) data[c * plane + i] = bytes[i * 3 + c];
        lock (_session)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var input = OrtValue.CreateTensorValueFromMemory(data, [1, 3, size, size]);
            using var options = new RunOptions();
            using var registration = cancellationToken.Register(() => options.Terminate = true);
            try
            {
                using var outputs = _session.Run(options, _session.InputNames, [input], _session.OutputNames);
                cancellationToken.ThrowIfCancellationRequested();
                long[] shape = outputs[0].GetTensorTypeAndShape().Shape;
                if (!shape.SequenceEqual(new long[] { 1, 2, size, size })) throw new InvalidDataException("DocScanner returned an invalid backward grid shape");
                return ApplyNormalizedGrid(raster, outputs[0].GetTensorDataAsSpan<float>().ToArray(), size, size, cancellationToken);
            }
            catch (OnnxRuntimeException) when (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    public static ScanRaster ApplyNormalizedGrid(ScanRaster raster, float[] grid, int gridWidth, int gridHeight, CancellationToken cancellationToken = default)
    {
        if (gridWidth < 2 || gridHeight < 2 || grid.Length != checked(gridWidth * gridHeight * 2) || grid.Any(v => !float.IsFinite(v)))
            throw new InvalidDataException("DocScanner returned an invalid normalized backward grid");
        if (raster.Width < 2 || raster.Height < 2 || (long)raster.Width * raster.Height > 40_000_000 || raster.Bgra32.LongLength != (long)raster.Width * raster.Height * 4)
            throw new InvalidDataException("Invalid rectification image dimensions");
        int length = checked(raster.Width * raster.Height);
        float[][] maps = [new float[length], new float[length]];
        for (int c = 0; c < 2; c++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            float[] channel = grid.AsSpan(c * gridWidth * gridHeight, gridWidth * gridHeight).ToArray();
            using var small = Mat.FromPixelData(gridHeight, gridWidth, MatType.CV_32FC1, channel);
            using var large = new Mat();
            Cv2.Resize(small, large, new Size(raster.Width, raster.Height), 0, 0, InterpolationFlags.Linear);
            Cv2.Blur(large, large, new Size(3, 3), borderType: BorderTypes.Reflect101);
            Marshal.Copy(large.Data, maps[c], 0, length);
        }
        byte[] output = new byte[checked(length * 4)];
        for (int y = 0; y < raster.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < raster.Width; x++)
            {
                int i = y * raster.Width + x;
                double px = (maps[0][i] + 1d) * .5 * (raster.Width - 1), py = (maps[1][i] + 1d) * .5 * (raster.Height - 1);
                int x0 = (int)Math.Floor(px), y0 = (int)Math.Floor(py);
                double fx = px - x0, fy = py - y0;
                for (int c = 0; c < 3; c++)
                {
                    double value = 0;
                    for (int dy = 0; dy < 2; dy++) for (int dx = 0; dx < 2; dx++)
                    {
                        int sx = x0 + dx, sy = y0 + dy;
                        if (sx >= 0 && sx < raster.Width && sy >= 0 && sy < raster.Height)
                            value += raster.Bgra32[(sy * raster.Width + sx) * 4 + c] * (dx == 0 ? 1 - fx : fx) * (dy == 0 ? 1 - fy : fy);
                    }
                    output[i * 4 + c] = (byte)Math.Clamp(Math.Round(value), 0, 255);
                }
                output[i * 4 + 3] = 255;
            }
        }
        return new(output, raster.Width, raster.Height);
    }

    public void Dispose() => _session.Dispose();
}
