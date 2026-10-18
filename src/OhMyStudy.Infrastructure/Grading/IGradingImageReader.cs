using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Grading;

public sealed record GradingImageResult(Image Image, uint Width, uint Height);
public interface IGradingImageReader
{
    Task<GradingImageResult> ReadAsync(byte[] bytes, Region? crop, ushort rotation, CancellationToken cancellationToken);
}
