using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace OhMyStudy.Documents;

public sealed record RasterizedPage(int Width, int Height, int Stride, byte[] Bgra32, double PageWidthPoints, double PageHeightPoints)
{
    /// <summary>A deterministic diagnostic BMP; UI can bind Bgra32 directly to a native bitmap.</summary>
    public void SaveBmp(string path)
    {
        using var stream = File.Create(path);
        using var writer = new BinaryWriter(stream);
        writer.Write((ushort)0x4d42); writer.Write(checked(54 + Bgra32.Length)); writer.Write(0); writer.Write(54);
        writer.Write(40); writer.Write(Width); writer.Write(-Height); writer.Write((ushort)1); writer.Write((ushort)32);
        writer.Write(0); writer.Write(Bgra32.Length); writer.Write(3780); writer.Write(3780); writer.Write(0); writer.Write(0);
        writer.Write(Bgra32);
    }
}

/// <summary>All PDFium calls share one lock, as required by PDFium's public API.</summary>
public sealed partial class PdfiumRasterizer
{
    public const string NativeSha256 = "79d4676b656cfb1abcea88f9ade3b4b0826c5200382db5f4ec72a636c598c118";
    internal static readonly object Gate = new();
    private static string? loadedPath;

    public PdfiumRasterizer(string libraryPath)
    {
        var path = Path.GetFullPath(libraryPath);
        lock (Gate)
        {
            if (loadedPath is not null)
            {
                if (!string.Equals(loadedPath, path, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException("PDFium 已从另一位置加载。 ");
                return;
            }
            using var stream = File.OpenRead(path);
            if (Convert.ToHexStringLower(SHA256.HashData(stream)) != NativeSha256) throw new InvalidDataException("PDFium 库哈希不匹配。 ");
            var module = NativeLibrary.Load(path);
            NativeLibrary.SetDllImportResolver(typeof(PdfiumRasterizer).Assembly,
                (name, _, _) => name == Native.LibraryName ? module : nint.Zero);
            Native.InitLibrary();
            // Process-wide module and initialization intentionally outlive individual documents.
            loadedPath = path;
        }
    }

    public PdfiumDocument Open(string path)
    {
        var file = new FileInfo(Path.GetFullPath(path));
        if (!file.Exists || file.Length is <= 0 or > 536_870_912) throw new InvalidDataException("PDF 文件不存在、为空或超过 512 MiB。 ");
        return Open(File.ReadAllBytes(file.FullName));
    }

    public unsafe PdfiumDocument Open(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is <= 0 or > 536_870_912) throw new InvalidDataException("PDF 数据为空或超过 512 MiB。 ");
        lock (Gate)
        {
            var data = NativeMemory.Alloc((nuint)bytes.Length);
            try
            {
                bytes.CopyTo(new Span<byte>(data, bytes.Length));
                var document = Native.LoadMemDocument64((nint)data, (nuint)bytes.Length, nint.Zero);
                if (document == nint.Zero) throw new InvalidDataException("PDFium 无法读取文件，错误码 " + Native.GetLastError());
                return new PdfiumDocument(document, (nint)data);
            }
            catch { NativeMemory.Free(data); throw; }
        }
    }

    internal static partial class Native
    {
        internal const string LibraryName = "OhMyStudy.Pdfium.Native";
        [LibraryImport(LibraryName, EntryPoint = "FPDF_InitLibrary")] internal static partial void InitLibrary();
        [LibraryImport(LibraryName, EntryPoint = "FPDF_LoadMemDocument64")] internal static partial nint LoadMemDocument64(nint data, nuint size, nint password);
        [LibraryImport(LibraryName, EntryPoint = "FPDF_GetLastError")] internal static partial uint GetLastError();
        [LibraryImport(LibraryName, EntryPoint = "FPDF_CloseDocument")] internal static partial void CloseDocument(nint document);
        [LibraryImport(LibraryName, EntryPoint = "FPDF_GetPageCount")] internal static partial int GetPageCount(nint document);
        [LibraryImport(LibraryName, EntryPoint = "FPDF_LoadPage")] internal static partial nint LoadPage(nint document, int index);
        [LibraryImport(LibraryName, EntryPoint = "FPDF_ClosePage")] internal static partial void ClosePage(nint page);
        [LibraryImport(LibraryName, EntryPoint = "FPDF_GetPageWidth")] internal static partial double GetPageWidth(nint page);
        [LibraryImport(LibraryName, EntryPoint = "FPDF_GetPageHeight")] internal static partial double GetPageHeight(nint page);
        [LibraryImport(LibraryName, EntryPoint = "FPDFBitmap_Create")] internal static partial nint CreateBitmap(int width, int height, int alpha);
        [LibraryImport(LibraryName, EntryPoint = "FPDFBitmap_Destroy")] internal static partial void DestroyBitmap(nint bitmap);
        [LibraryImport(LibraryName, EntryPoint = "FPDFBitmap_FillRect")] internal static partial int FillRect(nint bitmap, int left, int top, int width, int height, uint color);
        [LibraryImport(LibraryName, EntryPoint = "FPDFBitmap_GetBuffer")] internal static partial nint GetBitmapBuffer(nint bitmap);
        [LibraryImport(LibraryName, EntryPoint = "FPDFBitmap_GetStride")] internal static partial int GetBitmapStride(nint bitmap);
        [LibraryImport(LibraryName, EntryPoint = "FPDF_RenderPageBitmap")] internal static partial void RenderPage(nint bitmap, nint page, int x, int y, int width, int height, int rotation, int flags);
        [LibraryImport(LibraryName, EntryPoint = "FPDFText_LoadPage")] internal static partial nint LoadTextPage(nint page);
        [LibraryImport(LibraryName, EntryPoint = "FPDFText_ClosePage")] internal static partial void CloseTextPage(nint textPage);
        [LibraryImport(LibraryName, EntryPoint = "FPDFText_CountChars")] internal static partial int CountChars(nint textPage);
        [LibraryImport(LibraryName, EntryPoint = "FPDFText_GetText")] internal static unsafe partial int GetText(nint textPage, int start, int count, char* buffer);
    }
}

public sealed class PdfiumDocument : IDisposable
{
    private nint document;
    private nint data;
    internal PdfiumDocument(nint document, nint data) { this.document = document; this.data = data; }

    private void RequireOpen() => ObjectDisposedException.ThrowIf(document == nint.Zero, this);

    public int PageCount
    {
        get { lock (PdfiumRasterizer.Gate) { RequireOpen(); return PdfiumRasterizer.Native.GetPageCount(document); } }
    }

    public RasterizedPage RenderPage(int pageIndex, int maximumDimension = 1600, int quarterTurns = 0, CancellationToken cancellationToken = default)
    {
        if (maximumDimension is < 1 or > 8192) throw new ArgumentOutOfRangeException(nameof(maximumDimension));
        if (quarterTurns is < 0 or > 3) throw new ArgumentOutOfRangeException(nameof(quarterTurns));
        cancellationToken.ThrowIfCancellationRequested();
        lock (PdfiumRasterizer.Gate)
        {
            RequireOpen();
            if (pageIndex < 0 || pageIndex >= PdfiumRasterizer.Native.GetPageCount(document)) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            var page = PdfiumRasterizer.Native.LoadPage(document, pageIndex);
            if (page == nint.Zero) throw new InvalidDataException("PDFium 无法打开页面。 ");
            try
            {
                var widthPoints = PdfiumRasterizer.Native.GetPageWidth(page);
                var heightPoints = PdfiumRasterizer.Native.GetPageHeight(page);
                if (!double.IsFinite(widthPoints) || !double.IsFinite(heightPoints) || widthPoints <= 0 || heightPoints <= 0) throw new InvalidDataException("PDF 页面尺寸无效。 ");
                var width = widthPoints;
                var height = heightPoints;
                if (quarterTurns % 2 == 1) (width, height) = (height, width);
                var scale = maximumDimension / Math.Max(width, height);
                var pixelsWide = Math.Max(1, (int)Math.Round(width * scale));
                var pixelsHigh = Math.Max(1, (int)Math.Round(height * scale));
                if ((long)pixelsWide * pixelsHigh > 40_000_000) throw new InvalidDataException("PDF 栅格超过 4000 万像素。 ");
                var bitmap = PdfiumRasterizer.Native.CreateBitmap(pixelsWide, pixelsHigh, 1);
                if (bitmap == nint.Zero) throw new OutOfMemoryException("无法分配 PDF 栅格。 ");
                try
                {
                    PdfiumRasterizer.Native.FillRect(bitmap, 0, 0, pixelsWide, pixelsHigh, 0xffffffff);
                    // FPDF_ANNOT; do not initialize JavaScript, form actions or an interactive browser.
                    PdfiumRasterizer.Native.RenderPage(bitmap, page, 0, 0, pixelsWide, pixelsHigh, quarterTurns, 1);
                    cancellationToken.ThrowIfCancellationRequested();
                    var stride = PdfiumRasterizer.Native.GetBitmapStride(bitmap);
                    var bytes = new byte[checked(stride * pixelsHigh)];
                    Marshal.Copy(PdfiumRasterizer.Native.GetBitmapBuffer(bitmap), bytes, 0, bytes.Length);
                    return new(pixelsWide, pixelsHigh, stride, bytes, widthPoints, heightPoints);
                }
                finally { PdfiumRasterizer.Native.DestroyBitmap(bitmap); }
            }
            finally { PdfiumRasterizer.Native.ClosePage(page); }
        }
    }

    public unsafe string ExtractText(int pageIndex)
    {
        lock (PdfiumRasterizer.Gate)
        {
            RequireOpen();
            if (pageIndex < 0 || pageIndex >= PdfiumRasterizer.Native.GetPageCount(document)) throw new ArgumentOutOfRangeException(nameof(pageIndex));
            var page = PdfiumRasterizer.Native.LoadPage(document, pageIndex);
            if (page == nint.Zero) throw new InvalidDataException("PDF 页面无法读取。 ");
            try
            {
                var textPage = PdfiumRasterizer.Native.LoadTextPage(page);
                if (textPage == nint.Zero) throw new InvalidDataException("PDF 文字无法读取。 ");
                try
                {
                    var count = PdfiumRasterizer.Native.CountChars(textPage);
                    if (count is < 0 or > 2_000_000) throw new InvalidDataException("页面文字数无效。 ");
                    var characters = new char[checked(count + 1)];
                    fixed (char* buffer = characters)
                    {
                        var length = PdfiumRasterizer.Native.GetText(textPage, 0, count, buffer);
                        if (length <= 0) return "";
                        return new string(buffer, 0, Math.Min(length - 1, count));
                    }
                }
                finally { PdfiumRasterizer.Native.CloseTextPage(textPage); }
            }
            finally { PdfiumRasterizer.Native.ClosePage(page); }
        }
    }

    public unsafe void Dispose()
    {
        lock (PdfiumRasterizer.Gate)
        {
            if (document != nint.Zero) { PdfiumRasterizer.Native.CloseDocument(document); document = nint.Zero; }
            if (data != nint.Zero) { NativeMemory.Free((void*)data); data = nint.Zero; }
        }
        GC.SuppressFinalize(this);
    }
    ~PdfiumDocument() => Dispose();
}
