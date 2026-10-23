using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    public async Task<Asset> SaveGradingImageAsync(string jobId, byte[] bytes, CancellationToken cancellationToken = default)
    {
        ValidId(jobId);
        if (DetectMime(bytes) == "application/pdf") throw new InvalidDataException("Code outputs must be PNG, JPEG, or WebP images.");
        var prepared = (await PrepareAsync([new ImportSource("generated-image", bytes)], 1, cancellationToken).ConfigureAwait(false)).Single();
        return await Run(() =>
        {
            var created = new List<string>();
            try
            {
                return Transaction(cancellationToken, () =>
                {
                    var input = CheckRun(jobId);
                    WriteNew(prepared.OriginalFile, prepared.Bytes, created);
                    if (prepared.ThumbnailFile is { } thumbnail) WriteNew(thumbnail, prepared.Thumbnail!, created);
                    InsertAsset(prepared);
                    Execute("INSERT INTO question_assets(asset_id,document_id) VALUES($p0,$p1)", prepared.Id, input.Document.Id);
                    return Asset(prepared.Id);
                });
            }
            catch { CleanupCreated(created); throw; }
        }, cancellationToken, true).ConfigureAwait(false);
    }

    private void ValidateQuestionAssets(string documentId, Question question)
    {
        foreach (var image in question.Stem.Concat(question.Items.SelectMany(item => item.Prompt)).OfType<ImageContent>())
        {
            if (image.AssetId is not { } id) continue;
            ValidId(id);
            if (Scalar("SELECT 1 FROM question_assets WHERE asset_id=$p0 AND document_id=$p1", id, documentId) is null)
                throw new DomainValidationException("The processed image must be a code output saved for this document.");
        }
    }
}
