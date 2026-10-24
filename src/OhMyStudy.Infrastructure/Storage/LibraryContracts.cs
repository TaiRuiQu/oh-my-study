using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public enum LibraryOpenMode { CreateNew, ReadOnly, ReadWrite }

/// <summary>Opening an existing library for writing is an explicit caller decision.</summary>
public sealed record LibraryOpenOptions(string Directory, LibraryOpenMode Mode = LibraryOpenMode.ReadOnly)
{
    public ILibraryMediaProcessor? MediaProcessor { get; init; }
}

public sealed record ImportSource(string Name, byte[] Bytes, string Role = "material", byte[]? ProcessedImagePng = null);

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

public sealed record PaperSummary(Document Document, string? Subject, double? Earned, double? Possible, int QuestionCount);

public enum QuestionFilter { All, Mistakes, NeedsReview, Correct }
public enum QuestionSort { Newest, Oldest, Exam, LowestScore, RecentlyUpdated }
public sealed record QuestionQuery(QuestionFilter Filter = QuestionFilter.All, QuestionSort Sort = QuestionSort.Newest, string? Search = null, string? Subject = null, int Offset = 0, int Limit = 100);
public sealed record QuestionSummary(string Id, string DocumentId, string DocumentName, string Number, string Subject, string QuestionType, string Preview,
    int ItemCount, double? Earned, double? Possible, Verdict? Verdict, bool Mistake, bool NeedsReview);
public sealed record QuestionPage(IReadOnlyList<QuestionSummary> Items, int Matching, int Total);
