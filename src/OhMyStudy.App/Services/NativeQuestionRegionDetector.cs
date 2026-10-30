using System.Security.Cryptography;
using System.Text;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Documents;
using OhMyStudy.Domain;
using Windows.Graphics.Imaging;

namespace OhMyStudy.App.Services;

public sealed class NativeQuestionRegionDetector : IQuestionRegionDetector
{
    public Task<List<DetectedQuestionRegion>> DetectAsync(SourcePage page, byte[] image, string modelPath, CancellationToken cancellationToken)
        => NativeImageWork.RunAsync(async () =>
        {
            using var stream = await NativeImageCodec.OpenAsync(image, cancellationToken).ConfigureAwait(false);
            var decoder = await NativeImageCodec.CreateDecoderAsync(image, stream, cancellationToken).ConfigureAwait(false);
            NativeImageCodec.ValidateDimensions(decoder.PixelWidth, decoder.PixelHeight);
            var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, new BitmapTransform(),
                ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(cancellationToken).ConfigureAwait(false);
            var (rotated, width, height) = NativeImageCodec.Rotate(pixels.DetachPixelData(), decoder.PixelWidth, decoder.PixelHeight, page.Rotation, cancellationToken);
            using var detector = new QuestionRegionModel(modelPath);
            var boxes = detector.Detect(new ScanRaster(rotated, (int)width, (int)height), cancellationToken);
            string modelId = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(modelPath))))[..12];
            return boxes.Select((box, index) => new DetectedQuestionRegion($"{page.Id}-{modelId}-d{index}",
                SourceRegion(page, box),
                box.Confidence, Path.GetFileName(modelPath))).ToList();
        }, cancellationToken);

    private static Region SourceRegion(SourcePage page, QuestionBox box) => page.Rotation switch
    {
        0 => new() { PageId = page.Id, X = box.Left, Y = box.Top, Width = box.Right - box.Left, Height = box.Bottom - box.Top },
        90 => new() { PageId = page.Id, X = box.Top, Y = 1 - box.Right, Width = box.Bottom - box.Top, Height = box.Right - box.Left },
        180 => new() { PageId = page.Id, X = 1 - box.Right, Y = 1 - box.Bottom, Width = box.Right - box.Left, Height = box.Bottom - box.Top },
        270 => new() { PageId = page.Id, X = 1 - box.Bottom, Y = box.Left, Width = box.Bottom - box.Top, Height = box.Right - box.Left },
        _ => throw new InvalidDataException("Unsupported question region page rotation.")
    };
}
