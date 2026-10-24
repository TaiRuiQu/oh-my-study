using System.Text;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    private sealed record PreparedAsset(string Id, string Mime, byte[] Bytes, uint? Width, uint? Height,
        byte[]? Thumbnail, string Role, bool OriginalOnly = false, string? OriginalId = null, uint? PdfPageIndex = null)
    {
        public string OriginalFile => Id + (Mime switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "application/pdf" => ".pdf", _ => throw new InvalidDataException("Unsupported file type") });
        public string? ThumbnailFile => Thumbnail is null ? null : Id + ".thumb.png";
    }

    public async Task<Document> ImportDocumentAsync(string name, ImportMode mode, IReadOnlyList<ImportSource> sources, CancellationToken cancellationToken = default)
    {
        if (!Writable) throw new InvalidOperationException("The library is open read-only");
        if (string.IsNullOrWhiteSpace(name) || Encoding.UTF8.GetByteCount(name) > 512) throw new ArgumentException("Document names must be between 1 and 512 bytes");
        var prepared = await PrepareAsync(sources, LibraryLimits.MaximumPages, cancellationToken).ConfigureAwait(false);
        return await Run(() => WriteImport(NewId(), name, mode, prepared, cancellationToken), cancellationToken, true).ConfigureAwait(false);
    }

    public async Task<Document> AppendDocumentSourcesAsync(string documentId, IReadOnlyList<ImportSource> sources, CancellationToken cancellationToken = default)
    {
        ValidId(documentId);
        if (!Writable) throw new InvalidOperationException("The library is open read-only");
        var prepared = await PrepareAsync(sources, LibraryLimits.MaximumPages, cancellationToken).ConfigureAwait(false);
        return await Run(() => WriteImport(documentId, null, default, prepared, cancellationToken), cancellationToken, true).ConfigureAwait(false);
    }

    private async Task<List<PreparedAsset>> PrepareAsync(IReadOnlyList<ImportSource> sources, int maximumPages, CancellationToken cancellationToken)
    {
        var processor = options.MediaProcessor ?? throw new InvalidOperationException("File processing has not been configured");
        if (sources.Count is < 1 or > LibraryLimits.MaximumPages) throw new ArgumentException("Select between 1 and 12 files at a time");
        if (sources.Sum(s => (long)s.Bytes.Length + (s.ProcessedImagePng?.LongLength ?? 0)) > LibraryLimits.MaximumBatchBytes) throw new ArgumentException("This batch exceeds 256 MiB");
        foreach (var input in sources)
            if (input.Bytes.Length is < 1 or > LibraryLimits.MaximumAssetBytes || input.ProcessedImagePng is { Length: < 1 or > LibraryLimits.MaximumAssetBytes } || input.Role is not ("material" or "reference" or "student"))
                throw new ArgumentException("Invalid file size or page role");
        var submitted = sources.ToArray();
        await mediaGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await Task.Run(async () =>
            {
                var inputs = submitted.Select(s => s with { Bytes = s.Bytes.ToArray(), ProcessedImagePng = s.ProcessedImagePng?.ToArray() }).ToArray();
                var result = new List<PreparedAsset>(); var count = 0;
                foreach (var source in inputs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var magic = DetectMime(source.Bytes);
                    if (source.ProcessedImagePng is not null && (magic == "application/pdf" || DetectMime(source.ProcessedImagePng) != "image/png"))
                        throw new InvalidDataException("A processed photo must be a PNG derived from an image");
                    var media = await processor.PrepareAsync(source with { Bytes = (source.ProcessedImagePng ?? source.Bytes).ToArray(), ProcessedImagePng = null }, maximumPages - count, cancellationToken).ConfigureAwait(false);
                    if (media.Mime != (source.ProcessedImagePng is null ? magic : "image/png")) throw new InvalidDataException("File format verification failed");
                    var id = NewId();
                    if (magic == "application/pdf")
                    {
                        if (media.PdfPages is not { Count: > 0 }) throw new InvalidDataException("The PDF has no readable pages");
                        result.Add(new(id, magic, source.Bytes, null, null, null, source.Role, true));
                        for (var index = 0; index < media.PdfPages.Count; index++)
                        {
                            var page = media.PdfPages[index]; ValidateImage(page.Width, page.Height);
                            ValidatePng(page.PngBytes, page.Width, page.Height); ValidateThumbnail(page.ThumbnailPng);
                            result.Add(new(NewId(), "image/png", page.PngBytes, page.Width, page.Height, page.ThumbnailPng, source.Role, false, id, (uint)index)); count++;
                        }
                    }
                    else
                    {
                        ValidateImage(media.Width, media.Height);
                        if (source.ProcessedImagePng is not null) ValidatePng(source.ProcessedImagePng, media.Width!.Value, media.Height!.Value);
                        else if (magic == "image/png") ValidatePng(source.Bytes, media.Width!.Value, media.Height!.Value);
                        if (media.ThumbnailPng is null) throw new InvalidDataException("No page preview was generated");
                        ValidateThumbnail(media.ThumbnailPng);
                        if (source.ProcessedImagePng is not null)
                        {
                            result.Add(new(id, magic, source.Bytes, null, null, null, source.Role, true));
                            result.Add(new(NewId(), "image/png", source.ProcessedImagePng, media.Width, media.Height, media.ThumbnailPng, source.Role, false, id));
                        }
                        else result.Add(new(id, magic, source.Bytes, media.Width, media.Height, media.ThumbnailPng, source.Role));
                        count++;
                    }
                    if (count > maximumPages) throw new ArgumentException("Materials and reference answers may total at most 12 pages. The batch was not imported");
                    if (result.Sum(a => (long)a.Bytes.Length + (a.Thumbnail?.Length ?? 0)) > LibraryLimits.MaximumBatchBytes)
                        throw new ArgumentException("Original files and page caches exceed 256 MiB in total. The batch was not imported");
                }
                return result;
            }, cancellationToken).ConfigureAwait(false);
        }
        finally { mediaGate.Release(); }
    }

    private static string DetectMime(ReadOnlySpan<byte> bytes)
    {
        if (bytes.StartsWith("%PDF-"u8)) return "application/pdf";
        if (bytes.StartsWith(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 })) return "image/png";
        if (bytes.StartsWith(new byte[] { 255, 216, 255 })) return "image/jpeg";
        if (bytes.Length >= 12 && bytes[..4].SequenceEqual("RIFF"u8) && bytes.Slice(8, 4).SequenceEqual("WEBP"u8)) return "image/webp";
        throw new InvalidDataException("Select JPEG, PNG, WebP, or PDF files");
    }
    private static void ValidateImage(uint? width, uint? height)
    {
        if (width is null or 0 or > 20000 || height is null or 0 or > 20000 || (ulong)width.Value * height.Value > LibraryLimits.MaximumImagePixels)
            throw new InvalidDataException("Images must not exceed 40 million pixels or 20000 pixels on either side");
    }
    private static void ValidatePng(byte[] bytes, uint width, uint height)
    {
        if (bytes.Length is < 24 or > LibraryLimits.MaximumAssetBytes || DetectMime(bytes) != "image/png" ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4)) != width ||
            System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4)) != height) throw new InvalidDataException("Invalid page image");
    }
    private static void ValidateThumbnail(byte[] bytes)
    {
        if (bytes.Length < 24) throw new InvalidDataException("Invalid preview image");
        var width = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(16, 4));
        var height = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(20, 4));
        if (width is 0 or > 512 || height is 0 or > 512) throw new InvalidDataException("Invalid preview image dimensions");
        ValidatePng(bytes, width, height);
    }

    private Document WriteImport(string id, string? name, ImportMode mode, List<PreparedAsset> assets, CancellationToken cancellationToken)
    {
        var created = new List<string>();
        try
        {
            return Transaction(cancellationToken, () =>
            {
                if (name is null) { DocumentIdle(id); _ = Document(id); }
                var start = Convert.ToInt32(Scalar("SELECT COUNT(*) FROM source_pages WHERE document_id=$p0", id));
                if (start + assets.Count(a => !a.OriginalOnly) > LibraryLimits.MaximumPages) throw new InvalidOperationException("Materials and reference answers may total at most 12 pages");
                foreach (var asset in assets)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    WriteNew(asset.OriginalFile, asset.Bytes, created);
                    if (asset.ThumbnailFile is { } thumbnail) WriteNew(thumbnail, asset.Thumbnail!, created);
                }
                if (name is not null) Execute("INSERT INTO documents(id,name,mode,created_at,pending_pdf_pages) VALUES($p0,$p1,$p2,$p3,0)", id, name, DomainJson.Serialize(mode), Now());
                var ordinal = start;
                foreach (var asset in assets)
                {
                    InsertAsset(asset);
                    if (!asset.OriginalOnly) Execute("INSERT INTO source_pages(id,document_id,asset_id,page_index,rotation,preview_status,role,original_asset_id,pdf_page_index) VALUES($p0,$p1,$p2,$p3,0,'ready',$p4,$p5,$p6)", NewId(), id, asset.Id, ordinal++, asset.Role, asset.OriginalId, asset.PdfPageIndex);
                }
                if (assets.Any(asset => asset.Role == "student"))
                {
                    Execute("UPDATE documents SET mode=$p0 WHERE id=$p1 AND mode=$p2", DomainJson.Serialize(ImportMode.Auto), id, DomainJson.Serialize(ImportMode.Blank));
                    Execute("UPDATE paper_info SET earned=NULL WHERE document_id=$p0", id);
                }
                return Document(id);
            });
        }
        catch { CleanupCreated(created); throw; }
    }

    private void InsertAsset(PreparedAsset asset) => Execute("INSERT INTO assets(id,mime,byte_size,width,height,original_file,thumbnail_file) VALUES($p0,$p1,$p2,$p3,$p4,$p5,$p6)", asset.Id, asset.Mime, asset.Bytes.Length, asset.Width, asset.Height, asset.OriginalFile, asset.ThumbnailFile);

    public Task<Asset> GetAssetAsync(string assetId, CancellationToken cancellationToken = default)
    { ValidId(assetId); return Run(() => Asset(assetId), cancellationToken); }
    private Asset Asset(string id) => Query("SELECT id,mime,byte_size,width,height FROM assets WHERE id=$p0", r => new Asset
    { Id = r.GetString(0), Mime = r.GetString(1), ByteSize = checked((ulong)r.GetInt64(2)), Width = NullableUInt(r, 3), Height = NullableUInt(r, 4) }, id).SingleOrDefault() ?? throw new KeyNotFoundException("Original file not found");

    public Task<AssetData> ReadAssetAsync(string assetId, bool thumbnail = false, CancellationToken cancellationToken = default)
    { ValidId(assetId); return Run(() => ReadAsset(assetId, thumbnail), cancellationToken); }
    private AssetData ReadAsset(string id, bool thumbnail)
    {
        var asset = Asset(id);
        var name = Scalar(thumbnail ? "SELECT thumbnail_file FROM assets WHERE id=$p0" : "SELECT original_file FROM assets WHERE id=$p0", id) as string
            ?? throw new InvalidOperationException("The preview is not ready. The original file can still be read");
        var path = SafeAssetFile(name); var size = new FileInfo(path).Length;
        if (size is < 1 or > LibraryLimits.MaximumAssetBytes || !thumbnail && (ulong)size != asset.ByteSize) throw new InvalidDataException("The original file size does not match its record");
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length != size) throw new IOException("The file changed while being read");
        return new(thumbnail ? "image/png" : asset.Mime, bytes);
    }

    private string SafeAssetFile(string name)
    {
        ValidateFileName(name); RejectLinks(AssetRoot);
        var path = Path.GetFullPath(Path.Combine(AssetRoot, name));
        if (!File.Exists(path)) throw new FileNotFoundException("Original file not found");
        RejectLinks(path); return path;
    }
    private static void ValidateFileName(string name)
    {
        var stem = name.Split('.')[0].ToUpperInvariant();
        if (string.IsNullOrWhiteSpace(name) || name is "." or ".." || name.IndexOfAny(['/', '\\', ':']) >= 0 ||
            name != Path.GetFileName(name) || name.EndsWith('.') || name.EndsWith(' ') || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new IOException("Invalid original file path");
        if (stem is "CON" or "PRN" or "AUX" or "NUL" || stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && stem[3] is >= '1' and <= '9')
            throw new IOException("Invalid original file path");
    }
    private void WriteNew(string name, byte[] bytes, List<string> created)
    {
        ValidateFileName(name); RejectLinks(AssetRoot);
        var path = Path.Combine(AssetRoot, name);
        using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        created.Add(path); file.Write(bytes); file.Flush(true);
    }
    private void CleanupCreated(List<string> paths)
    {
        foreach (var path in paths)
        {
            try { RejectLinks(AssetRoot); if (File.Exists(path)) { RejectLinks(path); File.Delete(path); } }
            catch (IOException) { /* RecoverOrphanedAssetsAsync can report and remove survivors explicitly. */ }
        }
    }

    public Task<List<string>> RecoverOrphanedAssetsAsync(bool delete, CancellationToken cancellationToken = default) => Run(() =>
    {
        RejectLinks(AssetRoot);
        var referenced = Query("SELECT original_file FROM assets UNION SELECT thumbnail_file FROM assets WHERE thumbnail_file IS NOT NULL", r => r.GetString(0)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var orphans = Directory.EnumerateFiles(AssetRoot).Where(p => !referenced.Contains(Path.GetFileName(p))).ToList();
        foreach (var path in orphans)
        {
            cancellationToken.ThrowIfCancellationRequested(); RejectLinks(path);
            // Only our UUID-generated file names qualify; preserve unrelated user files.
            if (delete && Guid.TryParse(Path.GetFileName(path).Split('.')[0], out _)) File.Delete(path);
        }
        return orphans.Select(Path.GetFileName).OfType<string>().ToList();
    }, cancellationToken, delete);

    public async Task<Document> ParsePendingPdfsAsync(string documentId, CancellationToken cancellationToken = default)
    {
        ValidId(documentId);
        if (!Writable) throw new InvalidOperationException("The library is open read-only");
        var original = await GetDocumentAsync(documentId, cancellationToken).ConfigureAwait(false);
        if (!original.PendingPdfPages) return original;
        var prepared = new Dictionary<string, List<PreparedAsset>>(StringComparer.Ordinal);
        foreach (var page in original.Pages.Where(p => p.PreviewStatus == "pending_pdf"))
        {
            var data = await ReadAssetAsync(page.AssetId, false, cancellationToken).ConfigureAwait(false);
            var assets = await PrepareAsync([new("", data.Bytes, page.Role)], LibraryLimits.MaximumPages, cancellationToken).ConfigureAwait(false);
            prepared[page.Id] = assets.Where(a => !a.OriginalOnly).Select(a => a with { OriginalId = page.AssetId }).ToList();
        }
        var originalSizes = await Run(() => original.Pages.Sum(p => checked((long)Asset(p.AssetId).ByteSize)), cancellationToken).ConfigureAwait(false);
        if (originalSizes + prepared.Values.SelectMany(a => a).Sum(a => (long)a.Bytes.Length + (a.Thumbnail?.Length ?? 0)) > LibraryLimits.MaximumBatchBytes)
            throw new InvalidOperationException("Original files and page caches exceed 256 MiB in total. Existing materials were not changed");
        return await Run(() =>
        {
            var created = new List<string>();
            try
            {
                return Transaction(cancellationToken, () =>
                {
                    DocumentIdle(documentId);
                    if (!JsonEqual(DomainJson.Serialize(original), DomainJson.Serialize(Document(documentId)))) throw new InvalidOperationException("The document has changed. Retry");
                    if (original.Pages.Sum(p => prepared.TryGetValue(p.Id, out var images) ? images.Count : 1) > LibraryLimits.MaximumPages) throw new InvalidOperationException("The parsed PDF exceeds 12 pages. Import smaller batches");
                    Execute("UPDATE source_pages SET page_index=page_index+1000 WHERE document_id=$p0", documentId);
                    var ordinal = 0;
                    foreach (var page in original.Pages)
                    {
                        if (!prepared.TryGetValue(page.Id, out var images)) { Execute("UPDATE source_pages SET page_index=$p0 WHERE id=$p1", ordinal++, page.Id); continue; }
                        Execute("DELETE FROM source_pages WHERE id=$p0", page.Id);
                        for (var index = 0; index < images.Count; index++)
                        {
                            cancellationToken.ThrowIfCancellationRequested(); var asset = images[index];
                            WriteNew(asset.OriginalFile, asset.Bytes, created); WriteNew(asset.ThumbnailFile!, asset.Thumbnail!, created); InsertAsset(asset);
                            Execute("INSERT INTO source_pages(id,document_id,asset_id,page_index,rotation,preview_status,role,original_asset_id,pdf_page_index) VALUES($p0,$p1,$p2,$p3,$p4,'ready',$p5,$p6,$p7)", index == 0 ? page.Id : NewId(), documentId, asset.Id, ordinal++, page.Rotation, page.Role, page.AssetId, index);
                        }
                    }
                    Execute("UPDATE documents SET pending_pdf_pages=0 WHERE id=$p0", documentId);
                    return Document(documentId);
                });
            }
            catch { CleanupCreated(created); throw; }
        }, cancellationToken, true).ConfigureAwait(false);
    }
}
