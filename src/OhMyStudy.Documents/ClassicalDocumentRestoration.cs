using System.Runtime.InteropServices;
using OpenCvSharp;

namespace OhMyStudy.Documents;

public static class ClassicalDocumentRestoration
{
    public static ScanRaster Restore(ScanRaster raster, bool grayscale = false, CancellationToken cancellationToken = default, bool shadows = true, bool deblur = true, bool appearance = false)
    {
        if (raster.Width < 2 || raster.Height < 2 || (long)raster.Width * raster.Height > 40_000_000 || raster.Bgra32.LongLength != (long)raster.Width * raster.Height * 4)
            throw new InvalidDataException("Invalid restoration image dimensions");
        cancellationToken.ThrowIfCancellationRequested();
        int length = checked(raster.Width * raster.Height);
        var luminance = new byte[length];
        for (int y = 0; y < raster.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < raster.Width; x++)
            {
                int i = y * raster.Width + x, p = i * 4;
                luminance[i] = (byte)((raster.Bgra32[p] * 29 + raster.Bgra32[p + 1] * 150 + raster.Bgra32[p + 2] * 77) >> 8);
            }
        }
        using var source = Mat.FromPixelData(raster.Height, raster.Width, MatType.CV_8UC1, luminance);
        using var reduced = new Mat();
        double scale = Math.Min(1, 1000d / Math.Max(raster.Width, raster.Height));
        Cv2.Resize(source, reduced, new Size(Math.Max(2, (int)Math.Round(raster.Width * scale)), Math.Max(2, (int)Math.Round(raster.Height * scale))), 0, 0, InterpolationFlags.Area);
        cancellationToken.ThrowIfCancellationRequested();
        int radius = Math.Clamp(Math.Min(reduced.Width, reduced.Height) / 32, 3, 31);
        using var kernel = Cv2.GetStructuringElement(MorphShapes.Ellipse, new Size(radius * 2 + 1, radius * 2 + 1));
        using var background = new Mat();
        Cv2.MorphologyEx(reduced, background, MorphTypes.Close, kernel, borderType: BorderTypes.Reflect101);
        Cv2.GaussianBlur(background, background, new Size(0, 0), Math.Max(1, radius / 2d), borderType: BorderTypes.Reflect101);
        using var illumination = new Mat();
        Cv2.Resize(background, illumination, source.Size(), 0, 0, InterpolationFlags.Linear);
        var paper = new byte[length];
        Marshal.Copy(illumination.Data, paper, 0, length);
        var histogram = new int[256];
        foreach (byte value in paper) histogram[value]++;
        int remaining = (int)(length * .9), target = 0;
        while (target < 255 && (remaining -= histogram[target]) > 0) target++;
        target = Math.Min(245, target);
        var corrected = new float[length];
        for (int y = 0; y < raster.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < raster.Width; x++)
            {
                int i = y * raster.Width + x;
                double gain = shadows ? Math.Clamp(target / Math.Max(24d, paper[i]), 1, 1.6) : 1;
                corrected[i] = (float)Math.Min(255, luminance[i] * gain);
            }
        }
        using var observed = Mat.FromPixelData(raster.Height, raster.Width, MatType.CV_32FC1, corrected);
        using var estimate = observed.Clone();
        Cv2.MeanStdDev(observed, out _, out Scalar deviation);
        if (deblur && deviation.Val0 > .5)
        {
            using var blurred = new Mat();
            using var denominator = new Mat();
            using var ratio = new Mat();
            using var correction = new Mat();
            for (int iteration = 0; iteration < 3; iteration++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Cv2.GaussianBlur(estimate, blurred, new Size(5, 5), .65, borderType: BorderTypes.Reflect101);
                Cv2.Max(blurred, 1, denominator);
                Cv2.Divide(observed, denominator, ratio);
                Cv2.Min(ratio, 1.25, ratio);
                Cv2.Max(ratio, .8, ratio);
                Cv2.GaussianBlur(ratio, correction, new Size(5, 5), .65, borderType: BorderTypes.Reflect101);
                Cv2.Multiply(estimate, correction, estimate);
                Cv2.Min(estimate, 255, estimate);
            }
        }
        var restored = new float[length];
        Marshal.Copy(estimate.Data, restored, 0, length);
        byte[] output = new byte[checked(length * 4)];
        for (int y = 0; y < raster.Height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < raster.Width; x++)
            {
                int i = y * raster.Width + x, p = i * 4;
                double value = Math.Clamp(corrected[i] * .4 + restored[i] * .6, 0, 255);
                if (appearance) value = Math.Clamp((value - 128) * 1.08 + 128, 0, 255);
                double gain = Math.Clamp(value / Math.Max(1d, luminance[i]), .8, 1.8);
                for (int c = 0; c < 3; c++) output[p + c] = (byte)Math.Clamp(Math.Round(grayscale ? value : raster.Bgra32[p + c] * gain), 0, 255);
                output[p + 3] = 255;
            }
        }
        return new(output, raster.Width, raster.Height);
    }
}
