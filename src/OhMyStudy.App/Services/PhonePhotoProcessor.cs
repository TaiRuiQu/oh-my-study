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
            var raster = Rotate(await DecodeAsync(bytes, int.MaxValue, cancellationToken).ConfigureAwait(false), correction.Rotation, cancellationToken);
            var result = DocumentScanner.Process(raster.Bgra32, raster.Width, raster.Height,
                correction.Corners.Select(p => new ScanPoint(p[0], p[1])).ToArray(), "original", cancellationToken, int.MaxValue);
            if (correction.Enhancement is not ("original" or "color" or "gray")) throw new InvalidDataException("Unknown photo enhancement mode");
            if (correction.Backend is not ("standard" or "docres")) throw new InvalidDataException("Unknown photo restoration backend");
            string[] tasks = correction.Tasks ?? ["dewarping", "deshadowing", "deblurring", "appearance"];
            if (correction.Backend == "docres" && tasks.Length == 0) throw new InvalidDataException("Select at least one DocRes restoration task");
            if (tasks.Any(task => task is not ("dewarping" or "deshadowing" or "deblurring" or "appearance")) || tasks.Distinct().Count() != tasks.Length) throw new InvalidDataException("Invalid DocRes restoration tasks");
            if (correction.Enhancement == "original") tasks = tasks.Where(task => task == "dewarping").ToArray();
            if (correction.Backend == "standard")
            {
                Volatile.Write(ref _processingStage, "DocScanner: rectifying page…");
                result = Rectifier.Value.Rectify(result, cancellationToken);
                if (correction.Enhancement != "original")
                {
                    Volatile.Write(ref _processingStage, "OpenCV: removing shadows and recovering mild blur…");
                    result = ClassicalDocumentRestoration.Restore(result, correction.Enhancement == "gray", cancellationToken);
                }
            }
            else
            {
                try
                {
                    if (!tasks.Contains("dewarping", StringComparer.Ordinal))
                    {
                        Volatile.Write(ref _processingStage, "DocScanner: rectifying page…");
                        result = Rectifier.Value.Rectify(result, cancellationToken);
                    }
                    if (tasks.Length > 0)
                    {
                        Volatile.Write(ref _processingStage, "DocRes: loading local CUDA models…");
                        result = await DocResRestoration.RestoreAsync(result, tasks, cancellationToken, stage => Volatile.Write(ref _processingStage, "DocRes: " + stage), preCropped: true).ConfigureAwait(false);
                    }
                    if (correction.Enhancement == "gray")
                        for (int i = 0; i < result.Bgra32.Length; i += 4)
                            result.Bgra32[i] = result.Bgra32[i + 1] = result.Bgra32[i + 2] = (byte)((result.Bgra32[i] * 29 + result.Bgra32[i + 1] * 150 + result.Bgra32[i + 2] * 77) >> 8);
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    throw new PhotoRestorationException("DocRes restoration failed. Select standard restoration to continue safely.", error);
                }
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
