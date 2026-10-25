using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using OhMyStudy.App.Localization;
using OhMyStudy.App.Services;
using OhMyStudy.Documents;
using OhMyStudy.Infrastructure.Import;
using OhMyStudy.Infrastructure.Storage;

internal static class PhoneCaptureChecks
{
    public static async Task RunAsync(Func<string, Func<Task>, Task> check)
    {
        await PhoneCaptureProtocolChecks.RunAsync(check);
        await check("phone_all_processing_off_preserves_original_pixels_and_geometry", async () =>
        {
            const int width = 96, height = 64;
            var pixels = new byte[width * height * 4];
            for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
            {
                int p = (y * width + x) * 4;
                pixels[p] = (byte)(x * 2); pixels[p + 1] = (byte)(y * 3); pixels[p + 2] = (byte)(x + y); pixels[p + 3] = 255;
            }
            byte[] original = await NativeImageCodec.EncodePngAsync(pixels, width, height, default);
            var images = new NativePageImages();
            var corners = new[] { new[] { .2, .2 }, new[] { .8, .2 }, new[] { .8, .8 }, new[] { .2, .8 } };
            foreach (var correction in new[]
            {
                new PhotoCorrection(corners, "original", Processing: new()),
                new PhotoCorrection([], "gray", 90, "docres", [], new(Crop: true, Rectify: true, Grayscale: true))
            })
            {
                byte[] output = await PhonePhotoProcessor.ProcessAsync(original, correction, default);
                var raster = await images.RasterForUiAsync(output, 1000);
                Require(raster.Width == width && raster.Height == height && raster.Bgra32.SequenceEqual(pixels));
            }
            var preview = await PhonePhotoProcessor.PreviewAsync(original, default);
            Require(!preview.Detected && preview.Rotation == 0 && preview.Corners[0].SequenceEqual(new double[] { 0, 0 }));
            byte[] cropped = await PhonePhotoProcessor.ProcessAsync(original, new(corners, "original", Processing: new(Crop: true)), default);
            var croppedRaster = await images.RasterForUiAsync(cropped, 1000);
            Require(croppedRaster.Width < width && croppedRaster.Height < height);
        });
        await DocumentRestorationChecks.RunAsync(check);
        await check("phone_standard_pipeline_detects_crops_orients_then_restores", async () =>
        {
            var raster = Paper();
            byte[] original = await NativeImageCodec.EncodePngAsync(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, default);
            string model = Path.Combine(AppContext.BaseDirectory, "native", "scanner", "docaligner-lcnet.onnx");
            using var detector = new NeuralPageDetector(model);
            var detection = detector.Detect(raster);
            var expected = detection.Detected ? DocumentScanner.Process(raster.Bgra32, raster.Width, raster.Height, detection.Corners, "original", maximumDimension: int.MaxValue) : raster;
            using var orientation = new NeuralDocumentOrientation(Path.Combine(AppContext.BaseDirectory, "native", "scanner", "document-orientation.onnx"));
            var direction = orientation.Detect(expected);
            var rotated = NativeImageCodec.Rotate(expected.Bgra32, (uint)expected.Width, (uint)expected.Height, direction.Rotation, default);
            expected = ClassicalDocumentRestoration.Restore(new(rotated.Pixels, (int)rotated.Width, (int)rotated.Height), shadows: true, deblur: true, appearance: true);
            byte[] processed = await PhonePhotoProcessor.ProcessAsync(original, new([], "original", Processing: new(Crop: true, AutoOrientation: true, Shadows: true, Deblur: true, Appearance: true)), default);
            var actual = await new NativePageImages().RasterForUiAsync(processed, 1200);
            Require(actual.Width == expected.Width && actual.Height == expected.Height && actual.Bgra32.SequenceEqual(expected.Bgra32));
            Require((await new NativePageImages().RasterForUiAsync(original, 1200)).Bgra32.SequenceEqual(raster.Bgra32));
        });
        await check("scan_detects_skewed_page", () =>
        {
            var raster = Paper();
            byte[] original = raster.Bgra32.ToArray();
            var result = DocumentScanner.Detect(raster.Bgra32, raster.Width, raster.Height);
            Require(result.Detected);
            var expected = new[] { new ScanPoint(.15, .12), new(.85, .2), new(.78, .9), new(.1, .8) };
            for (int i = 0; i < 4; i++) Require(Math.Abs(result.Corners[i].X - expected[i].X) < .035 && Math.Abs(result.Corners[i].Y - expected[i].Y) < .035);
            Require(original.SequenceEqual(raster.Bgra32));
            return Task.CompletedTask;
        });
        await check("scan_full_frame_fallback_and_invalid_corners", () =>
        {
            var pixels = Enumerable.Repeat((byte)255, 100 * 80 * 4).ToArray();
            Require(!DocumentScanner.Detect(pixels, 100, 80).Detected);
            Require(!DocumentScanner.Detect(new byte[2 * 4000 * 4], 2, 4000).Detected);
            Reject(() => DocumentScanner.Process(pixels, 100, 80, [new(0, 0), new(1, 1), new(1, 0), new(0, 1)], "color"));
            Reject(() => DocumentScanner.Process(pixels, 100, 80, [new(0, 0), new(double.NaN, 0), new(1, 1), new(0, 1)], "color"));
            using var canceled = new CancellationTokenSource(); canceled.Cancel();
            try { DocumentScanner.Detect(pixels, 100, 80, canceled.Token); throw new InvalidOperationException("Scan ignored cancellation"); }
            catch (OperationCanceledException) { }
            return Task.CompletedTask;
        });
        await check("scan_perspective_rectifies_and_enhancement_preserves_color", () =>
        {
            var raster = Paper();
            var detection = DocumentScanner.Detect(raster.Bgra32, raster.Width, raster.Height);
            var result = DocumentScanner.Process(raster.Bgra32, raster.Width, raster.Height, detection.Corners, "original");
            Require(result.Width < raster.Width && result.Height < raster.Height);
            Require(result.Bgra32[(result.Height / 2 * result.Width + result.Width / 2) * 4] > 180);
            byte[] marked = Enumerable.Repeat((byte)180, 64 * 64 * 4).ToArray();
            for (int i = 3; i < marked.Length; i += 4) marked[i] = 255;
            for (int y = 20; y < 40; y++) for (int x = 20; x < 40; x++)
            { int offset = (y * 64 + x) * 4; marked[offset] = 20; marked[offset + 1] = 20; marked[offset + 2] = 200; }
            var color = DocumentScanner.Process(marked, 64, 64, DocumentScanner.FullFrame, "color");
            int center = (color.Height / 2 * color.Width + color.Width / 2) * 4;
            Require(color.Bgra32[center + 2] > color.Bgra32[center] + 100);
            var gray = DocumentScanner.Process(marked, 64, 64, DocumentScanner.FullFrame, "gray");
            Require(gray.Bgra32[center] == gray.Bgra32[center + 1] && gray.Bgra32[center + 1] == gray.Bgra32[center + 2]);
            return Task.CompletedTask;
        });
        await check("phone_capture_authenticated_upload_preview_and_original", async () =>
        {
            var errors = new List<Exception>();
            await using var server = new PhoneCaptureServer(PhonePhotoProcessor.AnalyzeAsync, PhonePhotoProcessor.ProcessAsync, errors.Add);
            await server.StartAsync(IPAddress.Loopback, "<!doctype html><title>Phone capture</title>");
            using var client = new HttpClient { BaseAddress = new Uri(server.Url.Split('#')[0]) };
            Require((await client.GetAsync("/")).IsSuccessStatusCode);
            Require((await client.GetAsync("/status")).StatusCode == HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Add("X-Capture-Token", server.Url.Split('#')[1]);
            var raster = Paper();
            byte[] png = await NativeImageCodec.EncodePngAsync(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, default);
            var response = await client.PostAsync("/upload?name=exam.png", new ByteArrayContent(png));
            Require(response.IsSuccessStatusCode);
            var info = JsonSerializer.Deserialize(await response.Content.ReadAsByteArrayAsync(), PhoneCaptureJsonContext.Default.PhonePhotoInfo)!;
            Require(!info.Pdf && info.Corners?.Length == 4 && server.Accepted.Count == 0);
            var pending = JsonSerializer.Deserialize(await client.GetByteArrayAsync("/status"), PhoneCaptureJsonContext.Default.PhoneCaptureStatus)!;
            Require(pending.Files.Count == 1 && !pending.Files[0].Ready && pending.Files[0].Corners?.Length == 4);
            Require((await client.GetAsync("/image/" + info.Id)).IsSuccessStatusCode);
            var correction = new PhotoCorrection(info.Corners!, "color", info.Rotation);
            foreach (var invalidMode in new[] { correction with { Backend = "unknown" }, correction with { Backend = "docres", Tasks = ["binarization"] } })
            {
                var rejected = await client.PostAsync("/render/" + info.Id, new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(invalidMode, PhoneCaptureJsonContext.Default.PhotoCorrection)));
                Require(rejected.StatusCode == HttpStatusCode.BadRequest);
            }
            errors.Clear();
            var rendered = await client.PostAsync("/render/" + info.Id, new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(correction, PhoneCaptureJsonContext.Default.PhotoCorrection)));
            Require(rendered.IsSuccessStatusCode && (await rendered.Content.ReadAsByteArrayAsync()).AsSpan().StartsWith(new byte[] { 137, 80, 78, 71 }));
            Require((await client.PostAsync("/accept/" + info.Id, null)).IsSuccessStatusCode);
            var sources = await server.TakeFilesAsync();
            Require(sources.Count == 1 && sources[0].Bytes.SequenceEqual(png) && sources[0].ProcessedImagePng is { Length: > 0 });
            var status = JsonSerializer.Deserialize(await client.GetByteArrayAsync("/status"), PhoneCaptureJsonContext.Default.PhoneCaptureStatus)!;
            Require(status.Files.Count == 1);
            Require((await client.PostAsync("/remove/" + info.Id, null)).IsSuccessStatusCode && server.Accepted.Count == 0);
            var invalid = await client.PostAsync("/upload?name=invalid.jpg", new ByteArrayContent([1, 2, 3]));
            Require(invalid.StatusCode == HttpStatusCode.BadRequest && errors.Count == 1);
            for (int i = 0; i < 12; i++) Require((await client.PostAsync("/upload?name=paper.pdf", new ByteArrayContent("%PDF-synthetic"u8.ToArray()))).IsSuccessStatusCode);
            Require((await client.PostAsync("/upload?name=extra.pdf", new ByteArrayContent("%PDF-synthetic"u8.ToArray()))).StatusCode == HttpStatusCode.BadRequest);
            Require(server.Accepted.Count == 0 && server.UploadedCount == 12);
            var staged = JsonSerializer.Deserialize(await client.GetByteArrayAsync("/status"), PhoneCaptureJsonContext.Default.PhoneCaptureStatus)!;
            var selection = new PhoneCaptureSelection(staged.Files.Select(file => file.Id).ToArray());
            Require((await client.PostAsync("/accept-batch", new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(selection, PhoneCaptureJsonContext.Default.PhoneCaptureSelection)))).IsSuccessStatusCode);
            string removedId = server.Accepted[0].Id;
            Require((await client.PostAsync("/remove/" + removedId, null)).IsSuccessStatusCode);
            Require((await client.PostAsync("/upload?name=last.pdf", new ByteArrayContent("%PDF-synthetic"u8.ToArray()))).IsSuccessStatusCode);
            staged = JsonSerializer.Deserialize(await client.GetByteArrayAsync("/status"), PhoneCaptureJsonContext.Default.PhoneCaptureStatus)!;
            Require((await client.PostAsync("/accept/" + staged.Files[^1].Id, null)).IsSuccessStatusCode);
            Require((await server.TakeFilesAsync())[^1].Name == "last.pdf");
        });
        await check("phone_capture_progress_and_cancel_preserve_pending_original", async () =>
        {
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            await using var server = new PhoneCaptureServer((_, _) => Task.FromResult(new PhonePhotoAnalysis([1], [[0, 0], [1, 0], [1, 1], [0, 1]], true)), async (_, _, cancellationToken) =>
            {
                entered.SetResult();
                await Task.Delay(Timeout.Infinite, cancellationToken);
                return [];
            }, error => throw new InvalidOperationException("Unexpected canceled request failure", error), processingStage: () => "DocRes: deblurring");
            await server.StartAsync(IPAddress.Loopback, "<!doctype html><title>Restoration regression</title>");
            using var client = new HttpClient { BaseAddress = new Uri(server.Url.Split('#')[0]), Timeout = TimeSpan.FromSeconds(5) };
            Require((await client.GetAsync("/progress")).StatusCode == HttpStatusCode.Unauthorized);
            client.DefaultRequestHeaders.Add("X-Capture-Token", server.Url.Split('#')[1]);
            var upload = await client.PostAsync("/upload?name=original.png", new ByteArrayContent([1, 2, 3]));
            var info = JsonSerializer.Deserialize(await upload.Content.ReadAsByteArrayAsync(), PhoneCaptureJsonContext.Default.PhonePhotoInfo)!;
            using var canceled = new CancellationTokenSource();
            var correction = new PhotoCorrection(info.Corners!, "original");
            var render = client.PostAsync("/render/" + info.Id, new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(correction, PhoneCaptureJsonContext.Default.PhotoCorrection)), canceled.Token);
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            Require(await client.GetStringAsync("/progress").WaitAsync(TimeSpan.FromSeconds(2)) == "DocRes: deblurring");
            await canceled.CancelAsync();
            try { await render; throw new InvalidOperationException("Processing request ignored cancellation"); }
            catch (OperationCanceledException) { }
            var status = JsonSerializer.Deserialize(await client.GetByteArrayAsync("/status"), PhoneCaptureJsonContext.Default.PhoneCaptureStatus)!;
            Require(status.Files.Count == 1 && !status.Files[0].Ready && status.Files[0].Id == info.Id && server.Accepted.Count == 0);
        });
    }

    private static IReadOnlyDictionary<string, byte[]> PhoneCaptureScripts()
    {
        var scripts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in new[] { "capture.js", "paper-detector.js", "paper-worker.js" })
        {
            using var resource = typeof(PhoneCaptureChecks).Assembly.GetManifestResourceStream("PhoneCapture.Script." + name)!;
            byte[] bytes = new byte[checked((int)resource.Length)];
            resource.ReadExactly(bytes);
            scripts.Add("/" + name, bytes);
        }
        return scripts;
    }

    public static async Task<int> PreviewAsync(bool https = false)
    {
        string output = Path.Combine(Environment.CurrentDirectory, "output", "phone-capture-preview");
        Directory.CreateDirectory(output);
        UiText.Initialize(Path.Combine(output, "language.local"), "en-US", Environment.GetEnvironmentVariable("OHMYSTUDY_CAPTURE_PREVIEW_LANGUAGE"));
        using var template = typeof(PhoneCaptureChecks).Assembly.GetManifestResourceStream("PhoneCapture.html")!;
        using var reader = new StreamReader(template);
        using var font = typeof(PhoneCaptureChecks).Assembly.GetManifestResourceStream("PhoneCapture.Font")!;
        using var fontBytes = new MemoryStream();
        await font.CopyToAsync(fontBytes);
        using var catalog = typeof(UiText).Assembly.GetManifestResourceStream("OhMyStudy.App.Localization.en-US.xml")!;
        var translations = XDocument.Load(catalog).Root!.Elements("text").Where(e => ((string)e.Attribute("key")!).StartsWith("PhoneCapture.", StringComparison.Ordinal))
            .ToDictionary(e => e.Value, e => UiText.GetByKey((string)e.Attribute("key")!));
        string html = (await reader.ReadToEndAsync()).Replace("__CAPTURE_TEXT__", JsonSerializer.Serialize(translations, PhoneCaptureJsonContext.Default.DictionaryStringString), StringComparison.Ordinal)
            .Replace("__CAPTURE_FONT__", Convert.ToBase64String(fontBytes.ToArray()), StringComparison.Ordinal)
            .Replace("__CAPTURE_LANGUAGE__", JsonSerializer.Serialize(UiText.Language, PhoneCaptureJsonContext.Default.String), StringComparison.Ordinal);
        await using var server = new PhoneCaptureServer(PhonePhotoProcessor.AnalyzeAsync, PhonePhotoProcessor.ProcessAsync, error => Console.Error.WriteLine(error), preview: PhonePhotoProcessor.PreviewAsync, scripts: PhoneCaptureScripts());
        using var certificate = https ? new PhoneCaptureCertificate(IPAddress.Loopback) : null;
        await server.StartAsync(IPAddress.Loopback, html, certificate: certificate?.Certificate, chain: certificate?.Chain);
        await File.WriteAllTextAsync(Path.Combine(output, "url.txt"), server.Url);
        await File.WriteAllTextAsync(Path.Combine(output, "http-url.txt"), server.HttpUrl);
        var raster = Paper();
        await File.WriteAllBytesAsync(Path.Combine(output, "paper.png"), await NativeImageCodec.EncodePngAsync(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, default));
        Console.WriteLine("Phone capture preview is running on loopback for twenty minutes.");
        await Task.Delay(TimeSpan.FromMinutes(20));
        return 0;
    }

    public static async Task<int> BenchmarkAsync(string[] paths)
    {
        string directory = Path.Combine(Environment.CurrentDirectory, "output", "phone-capture-real");
        Directory.CreateDirectory(directory);
        var results = new JsonArray();
        var images = new NativePageImages();
        foreach (string path in paths)
        {
            var watch = System.Diagnostics.Stopwatch.StartNew();
            byte[] original = await File.ReadAllBytesAsync(path);
            var analysis = await PhonePhotoProcessor.AnalyzeAsync(original, default);
            string name = Path.GetFileNameWithoutExtension(path);
            await File.WriteAllBytesAsync(Path.Combine(directory, name + ".preview.png"), analysis.PreviewPng);
            var points = new JsonArray(analysis.Corners.Select(point => (JsonNode)new JsonArray(point[0], point[1])).ToArray());
            var directionChecks = new JsonArray();
            var upright = await images.RasterForUiAsync(analysis.PreviewPng, 1200);
            foreach (ushort rotation in new ushort[] { 0, 90, 180, 270 })
            {
                var (rotatedPixels, rotatedWidth, rotatedHeight) = NativeImageCodec.Rotate(upright.Bgra32, (uint)upright.Width, (uint)upright.Height, rotation, default);
                byte[] rotated = await NativeImageCodec.EncodePngAsync(rotatedPixels, rotatedWidth, rotatedHeight, default);
                var direction = await PhonePhotoProcessor.AnalyzeAsync(rotated, default);
                int expected = (360 - rotation) % 360;
                directionChecks.Add((JsonNode)new JsonObject { ["inputRotation"] = rotation, ["expectedCorrection"] = expected, ["actualCorrection"] = direction.Rotation, ["confidence"] = direction.OrientationConfidence, ["passed"] = direction.Rotation == expected });
            }
            foreach (string mode in new[] { "original", "color", "gray" })
            {
                byte[] processed = await PhonePhotoProcessor.ProcessAsync(original, new(analysis.Corners, mode, analysis.Rotation), default);
                await File.WriteAllBytesAsync(Path.Combine(directory, name + "." + mode + ".png"), processed);
                var thumbnail = await images.RasterForUiAsync(processed, 1200);
                await File.WriteAllBytesAsync(Path.Combine(directory, name + "." + mode + ".preview.png"),
                    await NativeImageCodec.EncodePngAsync(thumbnail.Bgra32, (uint)thumbnail.Width, (uint)thumbnail.Height, default));
            }
            results.Add((JsonNode)new JsonObject { ["source"] = path, ["detected"] = analysis.Detected, ["corners"] = points, ["rotation"] = analysis.Rotation, ["orientationConfidence"] = analysis.OrientationConfidence, ["directionChecks"] = directionChecks, ["elapsedMs"] = watch.ElapsedMilliseconds });
            Console.WriteLine($"Photo benchmark: {name}; detected={analysis.Detected}; rotation={analysis.Rotation}; confidence={analysis.OrientationConfidence:F4}; elapsed={watch.ElapsedMilliseconds} ms");
        }
        await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), results.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return 0;
    }

    public static async Task<int> DocResBenchmarkAsync(string[] paths)
    {
        bool enabled = DocResRestoration.Status().Enabled;
        string directory = Path.Combine(Environment.CurrentDirectory, "output", "phone-capture-docres-real");
        Directory.CreateDirectory(directory);
        try
        {
            DocResRestoration.SetEnabled(true);
            var report = new JsonArray();
            foreach (string path in paths)
            {
                byte[] bytes = await File.ReadAllBytesAsync(path);
                var analysis = await PhonePhotoProcessor.AnalyzeAsync(bytes, default);
                foreach (string[] tasks in new string[][] { ["dewarping"], ["deshadowing"], ["deblurring"], ["appearance"], ["dewarping", "deshadowing", "deblurring", "appearance"] })
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    byte[] result = await PhonePhotoProcessor.ProcessAsync(bytes, new(analysis.Corners, "color", analysis.Rotation, "docres", tasks), default);
                    string output = Path.Combine(directory, Path.GetFileNameWithoutExtension(path) + "." + string.Join("-", tasks) + ".png");
                    await File.WriteAllBytesAsync(output, result);
                    report.Add((JsonNode)new JsonObject { ["source"] = path, ["tasks"] = string.Join(",", tasks), ["output"] = output, ["elapsedMs"] = watch.ElapsedMilliseconds, ["bytes"] = result.Length });
                    Console.WriteLine($"DocRes phone pipeline: {string.Join(",", tasks)}; elapsed={watch.ElapsedMilliseconds}ms; output={output}");
                }
            }
            await File.WriteAllTextAsync(Path.Combine(directory, "report.json"), report.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
            return 0;
        }
        finally { DocResRestoration.SetEnabled(enabled); }
    }

    private static ScanRaster Paper()
    {
        const int width = 400, height = 500;
        var corners = new[] { new ScanPoint(.15, .12), new(.85, .2), new(.78, .9), new(.1, .8) };
        var pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            bool inside = Enumerable.Range(0, 4).All(i =>
            {
                var a = corners[i]; var b = corners[(i + 1) % 4];
                return (b.X - a.X) * (y / (height - 1d) - a.Y) - (b.Y - a.Y) * (x / (width - 1d) - a.X) >= 0;
            });
            int p = (y * width + x) * 4;
            byte value = inside ? (byte)230 : (byte)35;
            if (inside && y > 150 && y < 360 && y % 30 < 3 && x > 100 && x < 280) value = 30;
            pixels[p] = pixels[p + 1] = pixels[p + 2] = value; pixels[p + 3] = 255;
        }
        return new(pixels, width, height);
    }

    private static void Reject(Action action)
    {
        try { action(); } catch (InvalidDataException) { return; }
        throw new InvalidOperationException("Invalid scan accepted");
    }

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Phone capture regression failed");
    }
}
