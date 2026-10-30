using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

public sealed record GradingImageResult(Image Image, uint Width, uint Height);
public interface IGradingImageReader
{
    Task<GradingImageResult> ReadAsync(byte[] bytes, Region? crop, ushort rotation, CancellationToken cancellationToken);
    Task<GradingImageResult> ReadLayoutAsync(byte[] bytes, ushort rotation, CancellationToken cancellationToken)
        => ReadAsync(bytes, null, rotation, cancellationToken);
    Task<GradingImageResult> ReadOcrAsync(byte[] bytes, ushort rotation, CancellationToken cancellationToken)
        => ReadAsync(bytes, null, rotation, cancellationToken);
}
