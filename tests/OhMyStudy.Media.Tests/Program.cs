using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.App.Services;
using OhMyStudy.Documents;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

var reportPath = args.ElementAtOrDefault(0) ?? "output/migration/media-tests.json";
var pdfium = args.ElementAtOrDefault(1) ?? Path.Combine(Environment.CurrentDirectory, "output/migration/toolchain/pdfium/pdfium.dll");
var results = new JsonArray();
int failures = 0;
var webpDecoder = args.ElementAtOrDefault(2) ?? Path.Combine(Environment.CurrentDirectory, "output/migration/toolchain/libwebp/libwebp-1.6.0-windows-x64/bin/dwebp.exe");
var images = new NativePageImages(webpDecoder);
var media = new NativeMediaProcessor(pdfium, webpDecoder);
using var fixtures = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures/assets.synthetic.json")));
byte[] originalPng = Convert.FromBase64String(fixtures.RootElement.GetProperty("assets")[0].GetProperty("base64").GetString()!);
byte[] originalPdf = Convert.FromBase64String(fixtures.RootElement.GetProperty("assets")[1].GetProperty("base64").GetString()!);

await Check("png_original_and_thumbnail", async () =>
{
    string hash = Convert.ToHexStringLower(SHA256.HashData(originalPng));
    var prepared = await media.PrepareAsync(new("synthetic.png", originalPng), 12, default);
    Assert(prepared.Mime == "image/png" && prepared.Width == 4 && prepared.Height == 3 && prepared.ThumbnailPng is { Length: > 0 });
    Assert(hash == Convert.ToHexStringLower(SHA256.HashData(originalPng)));
});

byte[] gridPixels = new byte[4 * 3 * 4];
for (int i = 0; i < 12; i++) { gridPixels[i * 4] = (byte)(i * 17); gridPixels[i * 4 + 1] = (byte)(255 - i * 13); gridPixels[i * 4 + 2] = (byte)(i * 11); gridPixels[i * 4 + 3] = 255; }
byte[] gridPng = await NativeImageCodec.EncodePngAsync(gridPixels, 4, 3, default);
foreach (ushort rotation in new ushort[] { 0, 90, 180, 270 })
{
    await Check("rotation_pixels_" + rotation, async () =>
    {
        var modelImage = await images.ReadAsync(gridPng, null, rotation, default);
        var raster = await images.RasterForUiAsync(Convert.FromBase64String(modelImage.Image.Base64), 100);
        var expected = NativeImageCodec.Rotate(gridPixels, 4, 3, rotation, default);
        Assert(modelImage.Width == expected.Width && modelImage.Height == expected.Height && raster.Bgra32.SequenceEqual(expected.Pixels));
        // Verify an independent pixel mapping, rather than only comparing the shared rotation routine.
        for (uint y = 0; y < 3; y++) for (uint x = 0; x < 4; x++)
        {
            var map = new PageViewportTransform(4, 3, rotation).SourceToViewport(new(x + 0.5, y + 0.5));
            int to = ((int)Math.Floor(map.Y) * raster.Width + (int)Math.Floor(map.X)) * 4;
            Assert(raster.Bgra32.AsSpan(to, 4).SequenceEqual(gridPixels.AsSpan((int)((y * 4 + x) * 4), 4)));
        }
    });
    await Check("normalized_coordinate_roundtrip_" + rotation, () =>
    {
        var transform = new PageViewportTransform(2400, 3600, rotation, 0.37, -42, 31);
        foreach (var point in new PagePoint[] { new(0, 0), new(1, 0), new(0, 1), new(1, 1), new(0.123, 0.789) })
        {
            var result = transform.ViewportToSourceNormalized(transform.SourceNormalizedToViewport(point));
            Assert(Math.Abs(result.X - point.X) < 1e-12 && Math.Abs(result.Y - point.Y) < 1e-12);
        }
        var region = transform.SourceRegionToViewport(new(0.1, 0.2, 0.3, 0.4));
        Assert(region.Width > 0 && region.Height > 0);
        return Task.CompletedTask;
    });
}

await Check("crop_uses_unrotated_source_then_rotates", async () =>
{
    var crop = new Region { PageId = "synthetic", X = 0.25, Y = 0, Width = 0.5, Height = 2d / 3 };
    var result = await images.ReadAsync(gridPng, crop, 90, default);
    var raster = await images.RasterForUiAsync(Convert.FromBase64String(result.Image.Base64), 100);
    Assert(result.Width == 2 && result.Height == 2);
    Assert(raster.Bgra32.AsSpan(0, 4).SequenceEqual(gridPixels.AsSpan((1 * 4 + 1) * 4, 4)));
    Assert(raster.Bgra32.AsSpan(4, 4).SequenceEqual(gridPixels.AsSpan(1 * 4, 4)));
});
await Check("crop_tolerance_rounding_clamping", () =>
{
    var crop = PageViewportTransform.CropBounds(new(0.1001, 0.2, 0.899901, 0.8), 1000, 500);
    Assert(crop == new PagePixelRect(100, 100, 900, 400));
    return Task.CompletedTask;
});
await Check("model_entire_page_dimensions_are_bounded", async () =>
{
    byte[] large = new byte[3000 * 4000 * 4];
    for (int i = 3; i < large.Length; i += 4) large[i] = 255;
    byte[] png = await NativeImageCodec.EncodePngAsync(large, 3000, 4000, default);
    var result = await images.ReadAsync(png, null, 0, default);
    Assert(result.Width == 2400 && result.Height == 3200);
    var turned = await images.ReadAsync(png, null, 90, default);
    Assert(turned.Width == 2400 && turned.Height == 1800);
    var crop = await images.ReadAsync(png, new Region { PageId = "synthetic", X = 0.1, Y = 0.1, Width = 0.9, Height = 0.9 }, 0, default);
    Assert(crop.Width == 2700 && crop.Height == 3600);
});
await Check("ui_raster_is_bounded", async () => { var raster = await images.RasterForUiAsync(gridPng, 2); Assert(raster.Width == 2 && raster.Height == 1); });
await Check("model_scan_compression_preserves_text_color_and_original", async () =>
{
    const int width = 1600, height = 2200;
    byte[] pixels = new byte[width * height * 4];
    var random = new Random(42);
    for (int offset = 0; offset < pixels.Length; offset += 4)
    {
        byte paper = (byte)random.Next(246, 256);
        pixels[offset] = paper; pixels[offset + 1] = paper; pixels[offset + 2] = paper; pixels[offset + 3] = 255;
    }
    for (int row = 0; row < 24; row++)
        for (int x = 100; x < 1500; x++)
        {
            int y = 160 + row * 75 + x % 23;
            for (int stroke = 0; stroke < 3; stroke++)
            {
                int offset = ((y + stroke) * width + x) * 4;
                pixels[offset] = 15; pixels[offset + 1] = 15; pixels[offset + 2] = 15;
            }
        }
    for (int y = 70; y < 110; y++) for (int x = 100; x < 400; x++)
    {
        int offset = (y * width + x) * 4;
        pixels[offset] = 10; pixels[offset + 1] = 10; pixels[offset + 2] = 220;
    }
    byte[] source = await NativeImageCodec.EncodePngAsync(pixels, width, height, default);
    byte[] hash = SHA256.HashData(source);
    Assert(source.Length > 500 * 1024);
    var result = await images.ReadAsync(source, null, 0, default);
    byte[] compressed = Convert.FromBase64String(result.Image.Base64);
    Console.WriteLine($"Scan compression: {source.Length} -> {compressed.Length} bytes; {result.Width}x{result.Height}");
    Assert(result.Image.Mime == "image/jpeg" && compressed.Length <= 200 * 1024 && compressed.Length < source.Length);
    Assert(Math.Max(result.Width, result.Height) >= 1280 && SHA256.HashData(source).SequenceEqual(hash));
    var raster = await images.RasterForUiAsync(compressed, height);
    Assert(raster.Width == result.Width && raster.Height == result.Height);
    int red = ((int)(90d * raster.Height / height) * raster.Width + (int)(200d * raster.Width / width)) * 4;
    Assert(raster.Bgra32[red + 2] > 180 && raster.Bgra32[red] < 40 && raster.Bgra32[red + 1] < 40);
    int ink = ((int)(179.5 * raster.Height / height) * raster.Width + (int)(110.5 * raster.Width / width)) * 4;
    Assert(raster.Bgra32[ink] < 70);
    var display = await images.ReadForDisplayAsync(source, null, 0);
    var displayRaster = await images.RasterForUiAsync(Convert.FromBase64String(display.Image.Base64), height);
    Assert(display.Image.Mime == "image/png" && displayRaster.Bgra32.SequenceEqual(pixels));
    string evidence = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(reportPath))!, "model-images");
    Directory.CreateDirectory(evidence);
    await File.WriteAllBytesAsync(Path.Combine(evidence, "scan-original.png"), source);
    await File.WriteAllBytesAsync(Path.Combine(evidence, "scan-model.jpg"), compressed);
});
await Check("model_noisy_crop_compression_reports_actual_rotated_dimensions", async () =>
{
    const int width = 1200, height = 1600;
    byte[] pixels = new byte[width * height * 4];
    new Random(73).NextBytes(pixels);
    for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 255;
    byte[] source = await NativeImageCodec.EncodePngAsync(pixels, width, height, default);
    var result = await images.ReadAsync(source, new Region { PageId = "synthetic", X = 0, Y = 0, Width = 1, Height = 1 }, 90, default);
    byte[] compressed = Convert.FromBase64String(result.Image.Base64);
    Assert(result.Image.Mime == "image/jpeg" && Math.Max(result.Width, result.Height) == 1280);
    Assert(result.Width < height && result.Height < width && Math.Abs(result.Width / (double)result.Height - height / (double)width) < 0.01);
    var raster = await images.RasterForUiAsync(compressed, height);
    Assert(raster.Width == result.Width && raster.Height == result.Height);
});
await Check("model_jpeg_composites_transparency_on_white", async () =>
{
    const int width = 1000, height = 1000;
    byte[] pixels = new byte[width * height * 4];
    new Random(17).NextBytes(pixels);
    for (int offset = 3; offset < pixels.Length; offset += 4) pixels[offset] = 0;
    byte[] source = await NativeImageCodec.EncodePngAsync(pixels, width, height, default);
    Assert(source.Length > 500 * 1024);
    var result = await images.ReadAsync(source, null, 0, default);
    var raster = await images.RasterForUiAsync(Convert.FromBase64String(result.Image.Base64), height);
    Assert(result.Image.Mime == "image/jpeg" && raster.Bgra32.All(value => value >= 254));
});
await Check("jpeg_native_decode_and_png_model_output", async () =>
{
    using var stream = new InMemoryRandomAccessStream();
    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream);
    encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, 4, 3, 96, 96, gridPixels);
    await encoder.FlushAsync();
    byte[] jpeg = await ReadBytes(stream);
    var prepared = await media.PrepareAsync(new("synthetic.jpg", jpeg), 12, default);
    Assert(prepared.Mime == "image/jpeg" && prepared.Width == 4 && prepared.Height == 3);
    Assert((await images.ReadAsync(jpeg, null, 0, default)).Image.Mime == "image/png");
});
await Check("webp_native_decode", async () =>
{
    // Google cwebp 1.6.0 -lossless -exact encoded the same original synthetic 4x3 PNG.
    // Google dwebp independently verified it; no codec executable is needed to run this fixture.
    byte[] webp = Convert.FromBase64String("UklGRh4AAABXRUJQVlA4TBEAAAAvA4AAAAdQwIIUuP+BiOh/AAA=");
    var prepared = await media.PrepareAsync(new("synthetic.webp", webp), 12, default);
    Assert(prepared.Mime == "image/webp" && prepared.Width == 4 && prepared.Height == 3);
    Assert((await images.ReadAsync(webp, null, 0, default)).Image.Mime == "image/png");
});
await Check("webp_pinned_fallback_and_container_limits", async () =>
{
    byte[] webp = Convert.FromBase64String("UklGRh4AAABXRUJQVlA4TBEAAAAvA4AAAAdQwIIUuP+BiOh/AAA=");
    Assert(NativeWebpDecoder.Dimensions(webp) == (4u, 3u));
    byte[] png = await NativeWebpDecoder.DecodeAsync(webp, webpDecoder, default);
    var raster = await images.RasterForUiAsync(png, 100);
    Assert(raster.Width == 4 && raster.Height == 3);
    await Reject(() => NativeWebpDecoder.DecodeAsync(webp[..^1], webpDecoder, default));
    byte[] oversized = (byte[])webp.Clone();
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(oversized.AsSpan(21, 4), (10_000u - 1) | ((10_000u - 1) << 14));
    await Reject(() => NativeWebpDecoder.DecodeAsync(oversized, webpDecoder, default));
    string scratch = Directory.CreateTempSubdirectory("OhMyStudy-Webp-Test-").FullName;
    string fake = Path.Combine(scratch, "dwebp.exe");
    try { await File.WriteAllBytesAsync(fake, [1, 2, 3]); await Reject(() => NativeWebpDecoder.DecodeAsync(webp, fake, default)); }
    finally { File.Delete(fake); Directory.Delete(scratch); }
});
await Check("webp_extended_canvas_must_match_encoded_dimensions", async () =>
{
    byte[] webp = Convert.FromBase64String("UklGRh4AAABXRUJQVlA4TBEAAAAvA4AAAAdQwIIUuP+BiOh/AAA=");
    byte[] extended = new byte[webp.Length + 18]; webp.AsSpan(0, 12).CopyTo(extended);
    System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(4, 4), (uint)extended.Length - 8);
    "VP8X"u8.CopyTo(extended.AsSpan(12)); System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(extended.AsSpan(16, 4), 10);
    webp.AsSpan(12).CopyTo(extended.AsSpan(30));
    await Reject(() => NativeWebpDecoder.DecodeAsync(extended, webpDecoder, default));
    extended[24] = 3; extended[27] = 2;
    Assert(NativeWebpDecoder.Dimensions(extended) == (4u, 3u));
    extended[20] = 2;
    await Reject(() => NativeWebpDecoder.DecodeAsync(extended, webpDecoder, default));
});
await Check("pdf_two_pages_preserve_original", async () =>
{
    byte[] before = SHA256.HashData(originalPdf);
    var prepared = await media.PrepareAsync(new("synthetic.pdf", originalPdf), 12, default);
    Assert(prepared.Mime == "application/pdf" && prepared.PdfPages?.Count == 2 && prepared.Width is null);
    foreach (var page in prepared.PdfPages!) { Assert(Math.Max(page.Width, page.Height) == 3600); Assert(page.PngBytes.Length > 0 && page.ThumbnailPng.Length > 0); }
    Assert(before.SequenceEqual(SHA256.HashData(originalPdf)));
});
await Check("pdf_page_limit", () => Reject(() => media.PrepareAsync(new("synthetic.pdf", originalPdf), 1, default)));
await Check("image_header_pixel_limit_before_decode", () =>
{
    bool rejected = false; try { NativeImageCodec.ValidateDimensions(20_000, 20_000); } catch (InvalidDataException) { rejected = true; }
    Assert(rejected); return Task.CompletedTask;
});
await Check("invalid_input_rejected", async () =>
{
    await Reject(() => media.PrepareAsync(new("fake.png", [1, 2, 3]), 12, default));
    await Reject(() => images.ReadAsync(gridPng, null, 45, default));
    await Reject(() => images.ReadAsync(gridPng, new Region { PageId = "synthetic", X = 1, Y = 0, Width = 1, Height = 1 }, 0, default));
});
await Check("cancelled_work_is_not_returned", async () =>
{
    using var cancel = new CancellationTokenSource(); cancel.Cancel();
    bool rejected = false; try { await images.ReadAsync(gridPng, null, 0, cancel.Token); } catch (OperationCanceledException) { rejected = true; }
    Assert(rejected);
});
await Check("processing_concurrency_is_two", async () =>
{
    int active = 0, max = 0;
    var allEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
    var tasks = Enumerable.Range(0, 6).Select(_ => NativeImageWork.RunAsync(async () =>
    {
        int now = Interlocked.Increment(ref active);
        InterlockedExtensions.Max(ref max, now);
        if (now == 2) allEntered.TrySetResult();
        try { await release.Task; return 1; } finally { Interlocked.Decrement(ref active); }
    }, default)).ToArray();
    try { await allEntered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert(max == 2); }
    finally { release.TrySetResult(); await Task.WhenAll(tasks); }
    Assert(max == 2);
});
await Check("byte_budget_and_lru_eviction", () =>
{
    var cache = new BoundedPageCache<string, string>(10, 3);
    cache.Set("a", "A", 4); cache.Set("b", "B", 4); Assert(cache.TryGet("a", out var a) && a == "A");
    cache.Set("c", "C", 4); Assert(!cache.TryGet("b", out _) && cache.RetainedBytes == 8);
    Assert(!cache.Set("large", "L", 11) && cache.RetainedBytes == 8);
    cache.Set("a", "new", 2); Assert(cache.RetainedBytes == 6);
    cache.Clear(); Assert(cache.RetainedBytes == 0 && cache.Count == 0); return Task.CompletedTask;
});
await Check("resolution_levels_share_budget", () =>
{
    var cache = new BoundedRasterPageCache(100);
    cache.Set(new("page", 100, 0), new(2, 2, 8, new byte[16], 2, 2));
    Assert(cache.TryGet(new("page", 50, 0), out var raster) && raster?.Width == 2);
    Assert(!cache.TryGet(new("page", 50, 90), out _));
    cache.Invalidate("page"); Assert(cache.Count == 0); return Task.CompletedTask;
});
await Check("cache_concurrent_budget_is_preserved", async () =>
{
    var cache = new BoundedPageCache<int, byte[]>(128, 8);
    await Task.WhenAll(Enumerable.Range(0, 8).Select(worker => Task.Run(() =>
    {
        for (int i = 0; i < 100; i++) { cache.Set(worker * 100 + i, new byte[16], 16); cache.TryGet(i, out _); }
    })));
    Assert(cache.RetainedBytes <= 128 && cache.Count <= 8);
});
using var process = Process.GetCurrentProcess();
bool coreClr = process.Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
var report = new JsonObject
{
    ["recorded_at"] = DateTimeOffset.UtcNow.ToString("O"),
    ["native_aot"] = !RuntimeFeature.IsDynamicCodeSupported && !coreClr,
    ["coreclr_loaded"] = coreClr,
    ["checks"] = results.Count,
    ["failures"] = failures,
    ["status"] = failures == 0 ? "passed" : "failed",
    ["results"] = results,
    ["not_tested"] = new JsonArray("Windows 10/11 external compatibility matrix", "Real user PDF/image library", "UI smoothness and process-wide performance budgets", "Printing")
};
Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(reportPath))!);
File.WriteAllText(reportPath, report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
Console.WriteLine($"Media checks: {results.Count}; failures: {failures}; Native AOT: {!RuntimeFeature.IsDynamicCodeSupported && !coreClr}");
Console.WriteLine($"Evidence: {Path.GetFullPath(reportPath)}");
return failures == 0 ? 0 : 1;

async Task Check(string id, Func<Task> action)
{
    try { await action(); results.Add((JsonNode)new JsonObject { ["id"] = id, ["status"] = "passed" }); }
    catch (Exception ex) { failures++; Console.WriteLine($"FAIL {id}: {ex}"); results.Add((JsonNode)new JsonObject { ["id"] = id, ["status"] = "failed", ["error"] = ex.ToString() }); }
}
static void Assert(bool condition) { if (!condition) throw new InvalidOperationException("Assertion failed"); }
static async Task Reject(Func<Task> action) { bool rejected = false; try { await action(); } catch (InvalidDataException) { rejected = true; } catch (ArgumentOutOfRangeException) { rejected = true; } Assert(rejected); }
static async Task<byte[]> ReadBytes(InMemoryRandomAccessStream stream)
{
    using var reader = new DataReader(stream.GetInputStreamAt(0)); uint size = (uint)stream.Size;
    Assert(await reader.LoadAsync(size) == size); byte[] bytes = new byte[size]; reader.ReadBytes(bytes); return bytes;
}
internal static class InterlockedExtensions
{
    public static void Max(ref int location, int value) { int current; do { current = Volatile.Read(ref location); if (current >= value) return; } while (Interlocked.CompareExchange(ref location, value, current) != current); }
}
