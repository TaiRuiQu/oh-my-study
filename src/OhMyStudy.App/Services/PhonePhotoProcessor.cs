using OhMyStudy.Documents;
using OhMyStudy.Infrastructure.Import;
using Windows.Graphics.Imaging;

namespace OhMyStudy.App.Services;

public static class PhonePhotoProcessor
{
    private static string _processingStage = "";
    public static string ProcessingStage => Volatile.Read(ref _processingStage);
    private static readonly Lazy<NeuralDocumentRectifier> Rectifier = new(() =>
        new(Path.Combine(AppContext.BaseDirectory, "native", "scanner", "docscanner-l.onnx")));
    private static readonly Lazy<NeuralPageDetector?> Detector = new(() =>
    {
        string path = Path.Combine(AppContext.BaseDirectory, "native", "scanner", "docaligner-lcnet.onnx");
        return File.Exists(path) ? new NeuralPageDetector(path) : null;
    });
    private static readonly Lazy<NeuralDocumentOrientation> Orientation = new(() =>
        new(Path.Combine(AppContext.BaseDirectory, "native", "scanner", "document-orientation.onnx")));

    public static Task<PhonePhotoAnalysis> PreviewAsync(byte[] bytes, CancellationToken cancellationToken) => NativeImageWork.RunAsync(async () =>
    {
        var raster = await DecodeAsync(bytes, 1200, cancellationToken).ConfigureAwait(false);
        byte[] png = await NativeImageCodec.EncodePngAsync(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, cancellationToken).ConfigureAwait(false);
        return new PhonePhotoAnalysis(png, [[0, 0], [1, 0], [1, 1], [0, 1]], false);
    }, cancellationToken);

    public static Task<PhonePhotoAnalysis> AnalyzeAsync(byte[] bytes, CancellationToken cancellationToken) => NativeImageWork.RunAsync(async () =>
    {
        var raster = await DecodeAsync(bytes, 1200, cancellationToken).ConfigureAwait(false);
        var detection = Detector.Value?.Detect(raster, cancellationToken) ?? DocumentScanner.Detect(raster.Bgra32, raster.Width, raster.Height, cancellationToken);
        var document = DocumentScanner.Process(raster.Bgra32, raster.Width, raster.Height, detection.Corners, "original", cancellationToken);
        var orientation = Orientation.Value.Detect(document, cancellationToken);
        if (orientation.Rotation != 0)
        {
            raster = Rotate(raster, orientation.Rotation, cancellationToken);
            detection = Detector.Value?.Detect(raster, cancellationToken) ?? DocumentScanner.Detect(raster.Bgra32, raster.Width, raster.Height, cancellationToken);
        }
        byte[] png = await NativeImageCodec.EncodePngAsync(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, cancellationToken).ConfigureAwait(false);
        return new PhonePhotoAnalysis(png, detection.Corners.Select(p => new[] { p.X, p.Y }).ToArray(), detection.Detected, orientation.Rotation, orientation.Confidence);
    }, cancellationToken);

    public static Task<byte[]> ProcessAsync(byte[] bytes, PhotoCorrection correction, CancellationToken cancellationToken) => NativeImageWork.RunAsync(async () =>
    {
        Volatile.Write(ref _processingStage, "");
        try
        {
            if (correction.Backend is not ("standard" or "docres")) throw new InvalidDataException("Unknown photo restoration backend");
            if (correction.Backend == "docres" && correction.Tasks is { Length: > 0 } tasks)
            {
                try
                {
                    Volatile.Write(ref _processingStage, "DocRes: loading local CUDA models…");
                    return await DocResRestoration.RestoreOriginalAsync(bytes, tasks, cancellationToken, stage => Volatile.Write(ref _processingStage, "DocRes: " + stage)).ConfigureAwait(false);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new PhotoRestorationException("DocRes restoration failed. The original photo is preserved.", error);
                }
            }
            var result = await DecodeAsync(bytes, int.MaxValue, cancellationToken).ConfigureAwait(false);
            if (correction.Backend == "standard")
            {
                var options = correction.Processing ?? new PhotoProcessingOptions();
                if (correction.Rotation != 0) result = Rotate(result, correction.Rotation, cancellationToken);
                if (options.Crop && !options.Rectify)
                {
                    var detection = correction.Corners.Length == 0
                        ? Detector.Value?.Detect(result, cancellationToken) ?? DocumentScanner.Detect(result.Bgra32, result.Width, result.Height, cancellationToken)
                        : new ScanDetection(correction.Corners.Select(point => new ScanPoint(point[0], point[1])).ToArray(), true);
                    if (detection.Detected)
                        result = DocumentScanner.Process(result.Bgra32, result.Width, result.Height, detection.Corners, "original", cancellationToken, int.MaxValue);
                }
                if (options.AutoOrientation)
                {
                    var orientation = Orientation.Value.Detect(result, cancellationToken);
                    result = Rotate(result, orientation.Rotation, cancellationToken);
                }
                if (options.Rectify)
                {
                    Volatile.Write(ref _processingStage, "DocScanner: rectifying page…");
                    result = Rectifier.Value.Rectify(result, cancellationToken);
                }
                if (options.Shadows || options.Deblur || options.Appearance || options.Grayscale)
                    result = ClassicalDocumentRestoration.Restore(result, options.Grayscale, cancellationToken, options.Shadows, options.Deblur, options.Appearance);
            }
            return await NativeImageCodec.EncodePngAsync(result.Bgra32, (uint)result.Width, (uint)result.Height, cancellationToken).ConfigureAwait(false);
        }
        finally { Volatile.Write(ref _processingStage, ""); }
    }, cancellationToken);

    private static ScanRaster Rotate(ScanRaster raster, ushort rotation, CancellationToken cancellationToken)
    {
        var (pixels, width, height) = NativeImageCodec.Rotate(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, rotation, cancellationToken);
        return new(pixels, (int)width, (int)height);
    }

    private static async Task<ScanRaster> DecodeAsync(byte[] bytes, int maximumDimension, CancellationToken cancellationToken)
    {
        using var stream = await NativeImageCodec.OpenAsync(bytes, cancellationToken).ConfigureAwait(false);
        var decoder = await NativeImageCodec.CreateDecoderAsync(bytes, stream, cancellationToken).ConfigureAwait(false);
        NativeImageCodec.ValidateDimensions(decoder.PixelWidth, decoder.PixelHeight);
        double scale = Math.Min(1, maximumDimension / (double)Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(2, (uint)Math.Round(decoder.PixelWidth * scale)), height = Math.Max(2, (uint)Math.Round(decoder.PixelHeight * scale));
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken).ConfigureAwait(false);
        var buffer = new Windows.Storage.Streams.Buffer(checked((uint)(bitmap.PixelWidth * bitmap.PixelHeight * 4)));
        bitmap.CopyToBuffer(buffer);
        using var reader = Windows.Storage.Streams.DataReader.FromBuffer(buffer);
        byte[] pixels = new byte[buffer.Length]; reader.ReadBytes(pixels);
        return new(pixels, bitmap.PixelWidth, bitmap.PixelHeight);
    }
}
