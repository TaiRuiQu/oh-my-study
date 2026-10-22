using OhMyStudy.Documents;
using OhMyStudy.Infrastructure.Storage;
using Windows.Graphics.Imaging;

namespace OhMyStudy.App.Services;

public sealed class NativeMediaProcessor(string? pdfiumPath = null, string? webpDecoderPath = null) : ILibraryMediaProcessor
{
    private readonly string _pdfiumPath = pdfiumPath ?? Path.Combine(AppContext.BaseDirectory, "native", "pdfium.dll");
    private readonly string _webpDecoderPath = webpDecoderPath ?? NativeWebpDecoder.DefaultPath;

    public Task<PreparedMedia> PrepareAsync(ImportSource source, int maximumPages, CancellationToken cancellationToken) => NativeImageWork.RunAsync(async () =>
    {
        if (maximumPages is < 1 or > LibraryLimits.MaximumPages) throw new ArgumentOutOfRangeException(nameof(maximumPages));
        string mime = NativeImageCodec.Mime(source.Bytes);
        if (mime == "application/pdf") return await PreparePdfAsync(source.Bytes, maximumPages, cancellationToken).ConfigureAwait(false);
        using var stream = await NativeImageCodec.OpenAsync(source.Bytes, cancellationToken).ConfigureAwait(false);
        var decoder = await NativeImageCodec.CreateDecoderAsync(source.Bytes, stream, cancellationToken, _webpDecoderPath).ConfigureAwait(false);
        NativeImageCodec.ValidateDimensions(decoder.PixelWidth, decoder.PixelHeight);
        double scale = Math.Min(1, 512d / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        uint width = Math.Max(1, (uint)Math.Floor(decoder.PixelWidth * scale)), height = Math.Max(1, (uint)Math.Floor(decoder.PixelHeight * scale));
        var pixels = (await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight,
            new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant },
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage).AsTask(cancellationToken).ConfigureAwait(false)).DetachPixelData();
        var thumbnail = await NativeImageCodec.EncodePngAsync(pixels, width, height, cancellationToken).ConfigureAwait(false);
        return new PreparedMedia(mime, decoder.PixelWidth, decoder.PixelHeight, thumbnail);
    }, cancellationToken);

    private async Task<PreparedMedia> PreparePdfAsync(byte[] bytes, int maximumPages, CancellationToken cancellationToken)
    {
        var rasterizer = new PdfiumRasterizer(_pdfiumPath);
        using var document = rasterizer.Open(bytes);
        int pageCount = document.PageCount;
        if (pageCount <= 0) throw new InvalidDataException(UiText.Get("PDF 没有可读取的页面。"));
        if (pageCount > maximumPages) throw new InvalidDataException(UiText.Format("本次最多还能导入 {0} 页，请拆分 PDF 后重试。", maximumPages));
        var prepared = new List<PreparedImage>(pageCount);
        long totalBytes = bytes.LongLength;
        for (int pageIndex = 0; pageIndex < pageCount; pageIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raster = document.RenderPage(pageIndex, maximumDimension: 3600, cancellationToken: cancellationToken);
            uint width = checked((uint)raster.Width), height = checked((uint)raster.Height);
            if (raster.Stride != checked(raster.Width * 4)) throw new InvalidDataException(UiText.Get("PDF 图片布局无效。"));
            byte[] png = await NativeImageCodec.EncodePngAsync(raster.Bgra32, width, height, cancellationToken).ConfigureAwait(false);
            double scale = Math.Min(1, 512d / Math.Max(width, height));
            uint thumbnailWidth = Math.Max(1, (uint)Math.Floor(width * scale)), thumbnailHeight = Math.Max(1, (uint)Math.Floor(height * scale));
            byte[] thumbnailPixels = NativeImageCodec.Resize(raster.Bgra32, width, height, thumbnailWidth, thumbnailHeight, cancellationToken);
            byte[] thumbnail = await NativeImageCodec.EncodePngAsync(thumbnailPixels, thumbnailWidth, thumbnailHeight, cancellationToken).ConfigureAwait(false);
            totalBytes = checked(totalBytes + png.LongLength + thumbnail.LongLength);
            if (totalBytes > LibraryLimits.MaximumBatchBytes) throw new InvalidDataException(UiText.Get("处理后的资料超过 256 MB，请拆分后重试。"));
            prepared.Add(new(png, width, height, thumbnail));
        }
        return new PreparedMedia("application/pdf", null, null, null, prepared);
    }
}
