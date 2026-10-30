using OhMyStudy.Domain;

namespace OhMyStudy.Application.Jobs;

public interface IQuestionRegionDetector
{
    Task<List<DetectedQuestionRegion>> DetectAsync(SourcePage page, byte[] image, string modelPath, CancellationToken cancellationToken);
}
