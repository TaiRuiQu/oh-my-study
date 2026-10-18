using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public enum LibraryOpenMode { CreateNew, ReadOnly, ReadWrite }

/// <summary>Opening an existing library for writing is an explicit caller decision.</summary>
public sealed record LibraryOpenOptions(string Directory, LibraryOpenMode Mode = LibraryOpenMode.ReadOnly)
{
    public ILibraryMediaProcessor? MediaProcessor { get; init; }
}

public sealed record ImportSource(string Name, byte[] Bytes, string Role = "material");

/// <summary>Decoding/rendering belongs outside the database thread. Originals are kept unchanged.</summary>
public interface ILibraryMediaProcessor
{
    Task<PreparedMedia> PrepareAsync(ImportSource source, int maximumPages, CancellationToken cancellationToken);
}

public sealed record PreparedImage(byte[] PngBytes, uint Width, uint Height, byte[] ThumbnailPng);
public sealed record PreparedMedia(string Mime, uint? Width, uint? Height, byte[]? ThumbnailPng,
    IReadOnlyList<PreparedImage>? PdfPages = null);
public sealed record AssetData(string Mime, byte[] Bytes);
public sealed record LibraryInspection(int SchemaVersion, bool IntegrityValid, IReadOnlyList<string> MissingAssets);
public sealed record BackupResult(string Directory, int AssetFileCount);

public static class LibraryLimits
{
    public const int MaximumPages = 12;
    public const int MaximumAssetBytes = 64 * 1024 * 1024;
    public const int MaximumBatchBytes = 256 * 1024 * 1024;
    public const ulong MaximumImagePixels = 40_000_000;
}
