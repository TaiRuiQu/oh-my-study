using OhMyStudy.Documents;
using OpenCvSharp;
using System.Runtime.InteropServices;

internal static class DocumentRestorationChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await check("restoration_constant_raster_and_color_preserved", () =>
        {
            var raster = Raster(96, 80, (_, _) => (80, 120, 160));
            var restored = ClassicalDocumentRestoration.Restore(raster);
            Require(restored.Bgra32.SequenceEqual(raster.Bgra32));
            return Task.CompletedTask;
        });
        await check("restoration_gradient_reduces_shadow_without_erasing_marks", () =>
        {
            var raster = Raster(256, 160, (x, y) =>
            {
                int value = 150 + x * 90 / 255;
                if (y is >= 75 and <= 77 && x is >= 40 and <= 220) value -= 35;
                return (value, value, value);
            });
            var restored = ClassicalDocumentRestoration.Restore(raster);
            int left = restored.Bgra32[(40 * 256 + 20) * 4], right = restored.Bgra32[(40 * 256 + 235) * 4];
            Require(right - left < 30);
            Require(restored.Bgra32[(76 * 256 + 100) * 4] + 15 < restored.Bgra32[(60 * 256 + 100) * 4]);
            Require(raster.Bgra32[(40 * 256 + 20) * 4] == 157);
            return Task.CompletedTask;
        });
        await check("restoration_gray_range_alpha_and_cancellation", () =>
        {
            var raster = Raster(64, 64, (x, _) => x < 32 ? (10, 20, 80) : (180, 210, 240));
            var result = ClassicalDocumentRestoration.Restore(raster, true);
            for (int i = 0; i < result.Bgra32.Length; i += 4)
                Require(result.Bgra32[i] == result.Bgra32[i + 1] && result.Bgra32[i + 1] == result.Bgra32[i + 2] && result.Bgra32[i + 3] == 255);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { ClassicalDocumentRestoration.Restore(raster, false, canceled.Token); throw new InvalidOperationException("Restoration ignored cancellation"); }
            catch (OperationCanceledException) { }
            return Task.CompletedTask;
        });
        await check("restoration_deconvolution_improves_known_defocus", () =>
        {
            var sharp = Raster(256, 160, (x, y) =>
            {
                int value = x is >= 40 and <= 223 && y is >= 25 and <= 134 && (x - 40) % 16 < 2 ? 45 : 220;
                return (value, value, value);
            });
            using var input = Mat.FromPixelData(160, 256, MatType.CV_8UC4, sharp.Bgra32);
            using var blur = new Mat();
            Cv2.GaussianBlur(input, blur, new Size(5, 5), .8, borderType: BorderTypes.Reflect101);
            byte[] bytes = new byte[sharp.Bgra32.Length];
            Marshal.Copy(blur.Data, bytes, 0, bytes.Length);
            var result = ClassicalDocumentRestoration.Restore(new(bytes, 256, 160));
            double before = 0, after = 0;
            for (int y = 20; y < 140; y++) for (int x = 25; x < 235; x++)
            {
                int i = (y * 256 + x) * 4;
                before += Math.Pow(bytes[i] - sharp.Bgra32[i], 2);
                after += Math.Pow(result.Bgra32[i] - sharp.Bgra32[i], 2);
                Require(result.Bgra32[i] <= 225);
            }
            Require(after < before * .85);
            return Task.CompletedTask;
        });
        await check("docscanner_grid_identity_interior_and_invalid_values", () =>
        {
            const int size = 32;
            var raster = Raster(size, size, (x, y) => (x * 7, y * 7, (x + y) * 3));
            var grid = new float[2 * size * size];
            for (int y = 0; y < size; y++) for (int x = 0; x < size; x++)
            {
                grid[y * size + x] = (float)(2d * x / (size - 1) - 1);
                grid[size * size + y * size + x] = (float)(2d * y / (size - 1) - 1);
            }
            var result = NeuralDocumentRectifier.ApplyNormalizedGrid(raster, grid, size, size);
            for (int y = 1; y < size - 1; y++) for (int x = 1; x < size - 1; x++)
                for (int c = 0; c < 4; c++) Require(result.Bgra32[(y * size + x) * 4 + c] == raster.Bgra32[(y * size + x) * 4 + c]);
            grid[0] = float.NaN;
            try { NeuralDocumentRectifier.ApplyNormalizedGrid(raster, grid, size, size); throw new InvalidOperationException("Invalid grid accepted"); }
            catch (InvalidDataException) { }
            try { using var model = new NeuralDocumentRectifier(Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".onnx")); throw new InvalidOperationException("Missing model accepted"); }
            catch (FileNotFoundException) { }
            return Task.CompletedTask;
        });
        await check("docres_gpu_strict_memory_discrete_runtime_and_multi_gpu", () =>
        {
            ulong limit = DocumentRestorationGpu.MinimumExclusiveMemoryBytes;
            Require(DocumentRestorationGpu.Select([new(0, "12 GiB", limit, false, true)]).Device is null);
            Require(DocumentRestorationGpu.Select([new(0, "Integrated", limit + 1, true, true)]).Device is null);
            Require(DocumentRestorationGpu.Select([new(0, "No runtime", limit + 1, false, false)]).Device is null);
            var chosen = DocumentRestorationGpu.Select([new(0, "12 GiB", limit, false, true), new(1, "16 GiB", 16UL << 30, false, true), new(2, "24 GiB unavailable", 24UL << 30, false, false)]);
            Require(chosen.Device?.Index == 1);
            Require(DocumentRestorationGpu.Select([new(0, "8 GiB", 8UL << 30, false, true), new(1, "8 GiB", 8UL << 30, false, true)]).Device is null);
            return Task.CompletedTask;
        });
        await check("docres_cancellation_precedes_runtime_probe_or_download", async () =>
        {
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            try { await DocResRestoration.RestoreAsync(Raster(4, 4, (_, _) => (128, 128, 128)), ["dewarping"], canceled.Token); throw new InvalidOperationException("DocRes ignored cancellation"); }
            catch (OperationCanceledException) { }
            try { await DocResRestoration.InstallAsync(_ => throw new InvalidOperationException("Canceled installation started"), canceled.Token); throw new InvalidOperationException("DocRes installation ignored cancellation"); }
            catch (OperationCanceledException) { }
        });
    }

    private static ScanRaster Raster(int width, int height, Func<int, int, (int B, int G, int R)> pixel)
    {
        var bytes = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * width + x) * 4;
            var value = pixel(x, y);
            bytes[i] = (byte)value.B; bytes[i + 1] = (byte)value.G; bytes[i + 2] = (byte)value.R; bytes[i + 3] = 255;
        }
        return new(bytes, width, height);
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Document restoration assertion failed");
    }
}
