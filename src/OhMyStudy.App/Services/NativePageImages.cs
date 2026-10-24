using OhMyStudy.Documents;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Grading;
using OhMyStudy.Infrastructure.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace OhMyStudy.App.Services;

/// <summary>Native codecs run off the UI thread; regions always address the unrotated source raster.</summary>
public sealed class NativePageImages(string? webpDecoderPath = null) : IGradingImageReader
{
    private const int ModelImageBytes = 200 * 1024;
    private const float ModelJpegQualityFloor = 0.6f;
    private const uint ModelImageLongSideFloor = 1280;
    private readonly string _webpDecoderPath = webpDecoderPath ?? NativeWebpDecoder.DefaultPath;
    public Task<GradingImageResult> ReadAsync(byte[] bytes, Region? crop, ushort rotation, CancellationToken cancellationToken) => ReadForModelAsync(bytes, crop, rotation, cancellationToken);

    public Task<GradingImageResult> ReadForModelAsync(byte[] bytes, Region? crop, ushort rotation, CancellationToken cancellationToken = default) => ReadImageAsync(bytes, crop, rotation, true, cancellationToken);

    public Task<GradingImageResult> ReadForDisplayAsync(byte[] bytes, Region? crop, ushort rotation, CancellationToken cancellationToken = default) => ReadImageAsync(bytes, crop, rotation, false, cancellationToken);

    private Task<GradingImageResult> ReadImageAsync(byte[] bytes, Region? crop, ushort rotation, bool compress, CancellationToken cancellationToken) => NativeImageWork.RunAsync(async () =>
    {
        PageViewportTransform.ValidateRotation(rotation);
        using var source = await NativeImageCodec.OpenAsync(bytes, cancellationToken).ConfigureAwait(false);
        var decoder = await NativeImageCodec.CreateDecoderAsync(bytes, source, cancellationToken, _webpDecoderPath).ConfigureAwait(false);
        NativeImageCodec.ValidateDimensions(decoder.PixelWidth, decoder.PixelHeight);
        PagePixelRect bounds = crop is null ? new(0, 0, decoder.PixelWidth, decoder.PixelHeight)
            : PageViewportTransform.CropBounds(new(crop.X, crop.Y, crop.Width, crop.Height), decoder.PixelWidth, decoder.PixelHeight);
        uint width = bounds.Width, height = bounds.Height;
        double scale = 1;
        if (crop is null)
        {
            bool turned = rotation is 90 or 270;
            double rotatedWidth = turned ? height : width, rotatedHeight = turned ? width : height;
            scale = Math.Min(1, Math.Min(2400d / rotatedWidth, 3600d / rotatedHeight));
        }
        uint decodedWidth = Math.Max(1, (uint)Math.Floor(width * scale));
        uint decodedHeight = Math.Max(1, (uint)Math.Floor(height * scale));
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        if (crop is null) { transform.ScaledWidth = decodedWidth; transform.ScaledHeight = decodedHeight; }
        else transform.Bounds = new BitmapBounds { X = bounds.X, Y = bounds.Y, Width = bounds.Width, Height = bounds.Height };
        var provider = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false);
        byte[] pixels = provider.DetachPixelData();
        NativeImageCodec.ValidatePixels(decodedWidth, decodedHeight, pixels);
        (pixels, uint finalWidth, uint finalHeight) = NativeImageCodec.Rotate(pixels, decodedWidth, decodedHeight, rotation, cancellationToken);
        var png = await NativeImageCodec.EncodePngAsync(pixels, finalWidth, finalHeight, cancellationToken).ConfigureAwait(false);
        if (compress && png.Length > ModelImageBytes)
            return await CompressAsync(pixels, finalWidth, finalHeight, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new GradingImageResult(new Image { Mime = "image/png", Base64 = Convert.ToBase64String(png) }, finalWidth, finalHeight);
    }, cancellationToken);

    private static async Task<GradingImageResult> CompressAsync(byte[] pixels, uint width, uint height, CancellationToken cancellationToken)
    {
        for (int y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (int x = 0; x < width; x++)
            {
                int offset = checked((y * (int)width + x) * 4);
                int alpha = pixels[offset + 3];
                for (int channel = 0; channel < 3; channel++)
                    pixels[offset + channel] = (byte)((pixels[offset + channel] * alpha + 255 * (255 - alpha) + 127) / 255);
                pixels[offset + 3] = 255;
            }
        }
        uint targetWidth = width, targetHeight = height;
        double floorScale = Math.Min(1, ModelImageLongSideFloor / (double)Math.Max(width, height));
        uint minWidth = Math.Max(1, (uint)Math.Floor(width * floorScale)), minHeight = Math.Max(1, (uint)Math.Floor(height * floorScale));
        while (true)
        {
            byte[] resized = NativeImageCodec.Resize(pixels, width, height, targetWidth, targetHeight, cancellationToken);
            byte[] jpeg = [];
            foreach (float quality in new[] { 0.8f, 0.7f, ModelJpegQualityFloor })
            {
                jpeg = await NativeImageCodec.EncodeJpegAsync(resized, targetWidth, targetHeight, quality, cancellationToken).ConfigureAwait(false);
                if (jpeg.Length <= ModelImageBytes)
                    return new(new Image { Mime = "image/jpeg", Base64 = Convert.ToBase64String(jpeg) }, targetWidth, targetHeight);
            }
            if (targetWidth <= minWidth && targetHeight <= minHeight)
                return new(new Image { Mime = "image/jpeg", Base64 = Convert.ToBase64String(jpeg) }, targetWidth, targetHeight);
            double scale = Math.Max(floorScale, Math.Min(0.9, Math.Sqrt(ModelImageBytes / (double)jpeg.Length) * 0.95));
            uint nextWidth = Math.Max(minWidth, (uint)Math.Floor(targetWidth * scale)), nextHeight = Math.Max(minHeight, (uint)Math.Floor(targetHeight * scale));
            if (nextWidth >= targetWidth && nextHeight >= targetHeight)
                return new(new Image { Mime = "image/jpeg", Base64 = Convert.ToBase64String(jpeg) }, targetWidth, targetHeight);
            targetWidth = nextWidth; targetHeight = nextHeight;
        }
    }

    public Task<RasterizedPage> RasterForUiAsync(byte[] bytes, int maximumDimension, CancellationToken cancellationToken = default) => NativeImageWork.RunAsync(async () =>
    {
        if (maximumDimension is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        using var source = await NativeImageCodec.OpenAsync(bytes, cancellationToken).ConfigureAwait(false);
        var decoder = await NativeImageCodec.CreateDecoderAsync(bytes, source, cancellationToken, _webpDecoderPath).ConfigureAwait(false);
        NativeImageCodec.ValidateDimensions(decoder.PixelWidth, decoder.PixelHeight);
        double scale = Math.Min(1, maximumDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(1, (uint)Math.Floor(decoder.PixelWidth * scale)), height = Math.Max(1, (uint)Math.Floor(decoder.PixelHeight * scale));
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false)).DetachPixelData();
        NativeImageCodec.ValidatePixels(width, height, pixels);
        cancellationToken.ThrowIfCancellationRequested();
        return new RasterizedPage((int)width, (int)height, checked((int)width * 4), pixels, decoder.PixelWidth * 72d / 96, decoder.PixelHeight * 72d / 96);
    }, cancellationToken);
}

internal static class NativeImageWork
{
    // Thread-pool workers are MTA on Windows. This bound includes decoding, pixel transforms and encoding.
    private static readonly SemaphoreSlim Slots = new(2, 2);
    public static async Task<T> RunAsync<T>(Func<Task<T>> action, CancellationToken cancellationToken)
    {
        await Slots.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return await Task.Run(action, cancellationToken).ConfigureAwait(false); }
        finally { Slots.Release(); }
    }
}

internal static class NativeImageCodec
{
    public static string Mime(byte[] bytes)
    {
        if (bytes.Length is <= 0 or > LibraryLimits.MaximumAssetBytes) throw new InvalidDataException(UiText.Get("The file is empty or exceeds 64 MB."));
        ReadOnlySpan<byte> data = bytes;
        if (data.StartsWith("%PDF-"u8)) return "application/pdf";
        if (data.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (data.Length >= 3 && data[0] == 255 && data[1] == 216 && data[2] == 255) return "image/jpeg";
        if (data.Length >= 12 && data[..4].SequenceEqual("RIFF"u8) && data.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        throw new InvalidDataException(UiText.Get("Choose a PNG, JPEG, WebP image, or PDF."));
    }

    public static async Task<InMemoryRandomAccessStream> OpenAsync(byte[] bytes, CancellationToken cancellationToken)
    {
        _ = Mime(bytes);
        var stream = new InMemoryRandomAccessStream();
        try
        {
            using var writer = new DataWriter(stream);
            writer.WriteBytes(bytes);
            await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false);
            writer.DetachStream(); stream.Seek(0); return stream;
        }
        catch { stream.Dispose(); throw; }
    }

    public static async Task<BitmapDecoder> CreateDecoderAsync(byte[] bytes, InMemoryRandomAccessStream stream, CancellationToken cancellationToken, string? webpDecoderPath = null)
    {
        string mime = Mime(bytes);
        if (mime == "image/webp")
        {
            var dimensions = NativeWebpDecoder.Dimensions(bytes);
            ValidateDimensions(dimensions.Width, dimensions.Height);
        }
        Guid decoder = mime switch
        {
            "image/png" => BitmapDecoder.PngDecoderId,
            "image/jpeg" => BitmapDecoder.JpegDecoderId,
            "image/webp" => BitmapDecoder.WebpDecoderId,
            _ => throw new InvalidDataException(UiText.Get("This operation requires an image."))
        };
        try { return await BitmapDecoder.CreateAsync(decoder, stream).AsTask(cancellationToken).ConfigureAwait(false); }
        catch (COMException) when (mime == "image/webp")
        {
            byte[] png = await NativeWebpDecoder.DecodeAsync(bytes, webpDecoderPath ?? NativeWebpDecoder.DefaultPath, cancellationToken).ConfigureAwait(false);
            stream.Seek(0); stream.Size = 0;
            using (var writer = new DataWriter(stream))
            {
                writer.WriteBytes(png); await writer.StoreAsync().AsTask(cancellationToken).ConfigureAwait(false); writer.DetachStream();
            }
            stream.Seek(0);
            return await BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, stream).AsTask(cancellationToken).ConfigureAwait(false);
        }
    }

    public static void ValidateDimensions(uint width, uint height)
    {
        if (width is 0 or > 20_000 || height is 0 or > 20_000 || (ulong)width * height > LibraryLimits.MaximumImagePixels)
            throw new InvalidDataException(UiText.Get("Image too large. Reduce it to under 40 megapixels and retry."));
    }

    public static void ValidatePixels(uint width, uint height, byte[] pixels)
    {
        ValidateDimensions(width, height);
        if (pixels.LongLength != checked((long)width * height * 4)) throw new InvalidDataException(UiText.Get("Image read incomplete. Select it again."));
    }

    public static (byte[] Pixels, uint Width, uint Height) Rotate(byte[] source, uint width, uint height, ushort rotation, CancellationToken cancellationToken)
    {
        PageViewportTransform.ValidateRotation(rotation); ValidatePixels(width, height, source);
        if (rotation == 0) return (source, width, height);
        uint targetWidth = rotation is 90 or 270 ? height : width, targetHeight = rotation is 90 or 270 ? width : height;
        var target = new byte[source.Length];
        for (uint y = 0; y < height; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            for (uint x = 0; x < width; x++)
            {
                (uint xx, uint yy) = rotation switch { 90 => (height - y - 1, x), 180 => (width - x - 1, height - y - 1), 270 => (y, width - x - 1), _ => throw new InvalidOperationException() };
                int from = checked((int)((y * width + x) * 4)), to = checked((int)((yy * targetWidth + xx) * 4));
                source.AsSpan(from, 4).CopyTo(target.AsSpan(to, 4));
            }
        }
        return (target, targetWidth, targetHeight);
    }

    public static Task<byte[]> EncodePngAsync(byte[] pixels, uint width, uint height, CancellationToken cancellationToken)
        => EncodeAsync(pixels, width, height, null, cancellationToken);

    public static Task<byte[]> EncodeJpegAsync(byte[] pixels, uint width, uint height, float quality, CancellationToken cancellationToken)
        => EncodeAsync(pixels, width, height, quality, cancellationToken);

    private static async Task<byte[]> EncodeAsync(byte[] pixels, uint width, uint height, float? quality, CancellationToken cancellationToken)
    {
        ValidatePixels(width, height, pixels);
        using var stream = new InMemoryRandomAccessStream();
        var encoder = quality is { } value
            ? await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream, new BitmapPropertySet { ["ImageQuality"] = new(value, Windows.Foundation.PropertyType.Single) }).AsTask(cancellationToken).ConfigureAwait(false)
            : await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(cancellationToken).ConfigureAwait(false);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, quality is null ? BitmapAlphaMode.Straight : BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask(cancellationToken).ConfigureAwait(false);
        if (stream.Size > LibraryLimits.MaximumAssetBytes) throw new InvalidDataException(UiText.Get("Processed image exceeds 64 MB. Resize it and retry."));
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        uint count = checked((uint)stream.Size);
        if (await reader.LoadAsync(count).AsTask(cancellationToken).ConfigureAwait(false) != count) throw new InvalidDataException(UiText.Get("Image save incomplete."));
        byte[] bytes = new byte[count]; reader.ReadBytes(bytes); cancellationToken.ThrowIfCancellationRequested(); return bytes;
    }

    public static byte[] Resize(byte[] source, uint width, uint height, uint targetWidth, uint targetHeight, CancellationToken cancellationToken)
    {
        ValidatePixels(width, height, source);
        if (width == targetWidth && height == targetHeight) return source;
        var pixels = new byte[checked((int)(targetWidth * targetHeight * 4))];
        for (uint y = 0; y < targetHeight; y++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            // Bilinear thumbnail sampling keeps the memory bound independent of the source dimensions.
            double sy = Math.Max(0, (y + 0.5) * height / targetHeight - 0.5);
            uint y0 = Math.Min(height - 1, (uint)sy), y1 = Math.Min(height - 1, y0 + 1); double dy = sy - y0;
            for (uint x = 0; x < targetWidth; x++)
            {
                double sx = Math.Max(0, (x + 0.5) * width / targetWidth - 0.5);
                uint x0 = Math.Min(width - 1, (uint)sx), x1 = Math.Min(width - 1, x0 + 1); double dx = sx - x0;
                for (uint channel = 0; channel < 4; channel++)
                {
                    double a = source[(y0 * width + x0) * 4 + channel] * (1 - dx) + source[(y0 * width + x1) * 4 + channel] * dx;
                    double b = source[(y1 * width + x0) * 4 + channel] * (1 - dx) + source[(y1 * width + x1) * 4 + channel] * dx;
                    pixels[(y * targetWidth + x) * 4 + channel] = (byte)Math.Clamp((int)Math.Round(a * (1 - dy) + b * dy), 0, 255);
                }
            }
        }
        return pixels;
    }
}

/// <summary>Fixed Google utility for systems whose registered WIC WebP codec cannot initialize.</summary>
internal static class NativeWebpDecoder
{
    public const string ExecutableSha256 = "17c1488bf84b7834e9aa908bb40afd0be2e55d57567d210e96336c56bb6ae993";
    public static string DefaultPath => Path.Combine(AppContext.BaseDirectory, "native", "webp", "dwebp.exe");

    public static (uint Width, uint Height) Dimensions(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length < 20 || !bytes[..4].SequenceEqual("RIFF"u8) || !bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) throw new InvalidDataException(UiText.Get("Invalid WebP format."));
        long end = 8L + BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(4, 4));
        if (end > bytes.Length || end < 20) throw new InvalidDataException(UiText.Get("Incomplete WebP image."));
        (uint Width, uint Height)? canvas = null, image = null;
        for (long cursor = 12; cursor + 8 <= end;)
        {
            int offset = checked((int)cursor);
            uint length = BinaryPrimitives.ReadUInt32LittleEndian(bytes.Slice(offset + 4, 4));
            long next = cursor + 8 + length + (length & 1);
            if (next > end || next <= cursor) throw new InvalidDataException(UiText.Get("Incomplete WebP image."));
            ReadOnlySpan<byte> payload = bytes.Slice(offset + 8, checked((int)length));
            var chunk = bytes.Slice(offset, 4);
            if (chunk.SequenceEqual("ANIM"u8) || chunk.SequenceEqual("ANMF"u8) || (chunk.SequenceEqual("VP8X"u8) && payload.Length > 0 && (payload[0] & 2) != 0))
                throw new InvalidDataException(UiText.Get("Save the animated WebP as a still image and retry."));
            if (chunk.SequenceEqual("VP8X"u8) && payload.Length >= 10)
            {
                if (canvas is not null) throw new InvalidDataException(UiText.Get("Invalid WebP format."));
                canvas = (1u + (uint)(payload[4] | payload[5] << 8 | payload[6] << 16), 1u + (uint)(payload[7] | payload[8] << 8 | payload[9] << 16));
                NativeImageCodec.ValidateDimensions(canvas.Value.Width, canvas.Value.Height);
            }
            if (chunk.SequenceEqual("VP8L"u8) && payload.Length >= 5 && payload[0] == 0x2f)
            {
                uint header = BinaryPrimitives.ReadUInt32LittleEndian(payload.Slice(1, 4));
                if ((header >> 29) != 0) throw new InvalidDataException(UiText.Get("Invalid WebP format."));
                if (image is not null) throw new InvalidDataException(UiText.Get("Invalid WebP format."));
                image = (1 + (header & 0x3fff), 1 + ((header >> 14) & 0x3fff));
                NativeImageCodec.ValidateDimensions(image.Value.Width, image.Value.Height);
            }
            if (chunk.SequenceEqual("VP8 "u8) && payload.Length >= 10 && payload.Slice(3, 3).SequenceEqual(new byte[] { 0x9d, 0x01, 0x2a }))
            {
                if (image is not null) throw new InvalidDataException(UiText.Get("Invalid WebP format."));
                image = ((uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(6, 2)) & 0x3fff), (uint)(BinaryPrimitives.ReadUInt16LittleEndian(payload.Slice(8, 2)) & 0x3fff));
                NativeImageCodec.ValidateDimensions(image.Value.Width, image.Value.Height);
            }
            cursor = next;
        }
        if (image is { } actual && (canvas is null || canvas == image)) return actual;
        throw new InvalidDataException(UiText.Get("WebP dimensions are missing."));
    }

    public static async Task<byte[]> DecodeAsync(byte[] bytes, string decoderPath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var expected = Dimensions(bytes); NativeImageCodec.ValidateDimensions(expected.Width, expected.Height);
        var executable = Path.GetFullPath(decoderPath);
        if (!File.Exists(executable)) throw new InvalidDataException(UiText.Get("Could not read WebP. Reinstall the app and retry."));
        await using (var check = File.OpenRead(executable))
            if (Convert.ToHexStringLower(await SHA256.HashDataAsync(check, cancellationToken).ConfigureAwait(false)) != ExecutableSha256)
                throw new InvalidDataException(UiText.Get("Image component validation failed. Reinstall the app and retry."));
        string directory = Directory.CreateTempSubdirectory("OhMyStudy-Webp-").FullName;
        string input = Path.Combine(directory, "input.webp"), output = Path.Combine(directory, "output.png");
        try
        {
            await File.WriteAllBytesAsync(input, bytes, cancellationToken).ConfigureAwait(false);
            using var process = new Process { StartInfo = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = directory, RedirectStandardOutput = true, RedirectStandardError = true } };
            process.StartInfo.ArgumentList.Add("-quiet"); process.StartInfo.ArgumentList.Add(input); process.StartInfo.ArgumentList.Add("-o"); process.StartInfo.ArgumentList.Add(output);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            if (!process.Start()) throw new InvalidDataException(UiText.Get("Could not read image. Try again."));
            Task stdout = ReadBoundedAsync(process.StandardOutput, timeout.Token), stderr = ReadBoundedAsync(process.StandardError, timeout.Token);
            Task monitor = MonitorOutputAsync(process, output, timeout.Token);
            try
            {
                await Task.WhenAll(process.WaitForExitAsync(timeout.Token), stdout, stderr, monitor).ConfigureAwait(false);
                if (process.ExitCode != 0 || !File.Exists(output)) throw new InvalidDataException(UiText.Get("Could not read WebP. Check that the file is complete."));
                var info = new FileInfo(output);
                if (info.Length is <= 0 or > LibraryLimits.MaximumAssetBytes) throw new InvalidDataException(UiText.Get("Processed image exceeds 64 MB. Resize it and retry."));
                byte[] png = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
                if (png.Length < 24 || !png.AsSpan(0, 8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })
                    || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(16, 4)) != expected.Width || BinaryPrimitives.ReadUInt32BigEndian(png.AsSpan(20, 4)) != expected.Height)
                    throw new InvalidDataException(UiText.Get("Invalid image read result. Try again."));
                return png;
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidDataException(UiText.Get("Image read timed out. Resize or crop it and retry.")); }
            finally
            {
                timeout.Cancel();
                KillOwned(process);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
                try { await Task.WhenAll(stdout, stderr, monitor).ConfigureAwait(false); } catch (Exception error) when (error is OperationCanceledException or InvalidDataException) { }
            }
        }
        finally
        {
            // Delete only the two known files and the exact freshly-created empty directory.
            try { File.Delete(input); File.Delete(output); Directory.Delete(directory, recursive: false); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static async Task ReadBoundedAsync(StreamReader reader, CancellationToken cancellationToken)
    {
        char[] buffer = new char[2048]; int total = 0, read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
            if ((total += read) > 16_384) throw new InvalidDataException(UiText.Get("Could not read image. Check that the file is complete."));
    }

    private static async Task MonitorOutputAsync(Process process, string output, CancellationToken cancellationToken)
    {
        while (!process.HasExited)
        {
            if (File.Exists(output) && new FileInfo(output).Length > LibraryLimits.MaximumAssetBytes)
            {
                KillOwned(process);
                throw new InvalidDataException(UiText.Get("Processed image exceeds 64 MB. Resize it and retry."));
            }
            await Task.Delay(40, cancellationToken).ConfigureAwait(false);
        }
    }

    private static void KillOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { /* This owned process exited between the state check and kill. */ }
    }
}
