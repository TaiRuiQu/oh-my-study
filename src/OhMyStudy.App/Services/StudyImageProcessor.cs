using OhMyStudy.Documents;
using OhMyStudy.Infrastructure.Import;
using Windows.Graphics.Imaging;

namespace OhMyStudy.App.Services;

public static class StudyImageProcessor
{
    private static string _processingStage = "";
    public static string ProcessingStage => Volatile.Read(ref _processingStage);
    private static readonly Lazy<TrainedDocumentRestoration> Restoration = new(() =>
        new(Path.Combine(AppContext.BaseDirectory, "native", "restoration")));
    private static readonly Lazy<NeuralDocumentOrientation> Orientation = new(() =>
        new(Path.Combine(AppContext.BaseDirectory, "native", "restoration", "orientation.onnx")));

    public static Task<PhonePhotoAnalysis> PreviewAsync(byte[] bytes, CancellationToken cancellationToken) => NativeImageWork.RunAsync(async () =>
    {
        var raster = await DecodeAsync(bytes, 1200, cancellationToken).ConfigureAwait(false);
        byte[] png = await NativeImageCodec.EncodePngAsync(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, cancellationToken).ConfigureAwait(false);
        return new PhonePhotoAnalysis(png);
    }, cancellationToken);

    public static Task<byte[]> ProcessAsync(byte[] bytes, PhotoCorrection correction, CancellationToken cancellationToken) => NativeImageWork.RunAsync(async () =>
    {
        Volatile.Write(ref _processingStage, "");
        try
        {
            var raster = await DecodeAsync(bytes, int.MaxValue, cancellationToken).ConfigureAwait(false);
            var options = correction.Processing ?? new PhotoProcessingOptions();
            if (options.AutoOrientation && correction.Rotation == 0)
            {
                Volatile.Write(ref _processingStage, "Orienting page…");
                var orientation = Orientation.Value.Detect(raster, cancellationToken);
                if (orientation.Rotation != 0)
                {
                    var oriented = NativeImageCodec.Rotate(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, orientation.Rotation, cancellationToken);
                    raster = new(oriented.Pixels, (int)oriented.Width, (int)oriented.Height);
                }
            }
            if (correction.Rotation != 0)
            {
                var rotated = NativeImageCodec.Rotate(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, correction.Rotation, cancellationToken);
                raster = new(rotated.Pixels, (int)rotated.Width, (int)rotated.Height);
            }
            if (options.Rectify || options.Appearance)
                raster = Restoration.Value.Restore(raster, options.Rectify, options.Appearance, cancellationToken, stage => Volatile.Write(ref _processingStage, stage));
            return await NativeImageCodec.EncodePngAsync(raster.Bgra32, (uint)raster.Width, (uint)raster.Height, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception error) when (error is not OperationCanceledException)
        {
            throw new PhotoRestorationException($"Trained image correction failed. InputBytes={bytes.Length}; Rotation={correction.Rotation}; AutoOrientation={correction.Processing?.AutoOrientation ?? true}; Geometry={correction.Processing?.Rectify ?? true}; WhiteBackground={correction.Processing?.Appearance ?? true}. The original image is preserved.", error);
        }
        finally { Volatile.Write(ref _processingStage, ""); }
    }, cancellationToken);

    public static void Dispose()
    {
        if (Restoration.IsValueCreated) Restoration.Value.Dispose();
        if (Orientation.IsValueCreated) Orientation.Value.Dispose();
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
