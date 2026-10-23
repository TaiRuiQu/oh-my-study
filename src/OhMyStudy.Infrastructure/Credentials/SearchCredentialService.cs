using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Credentials;

public sealed record SearchSettingsSaveResult(AppSettings Settings, bool CredentialCleanupRequired);

public sealed class SearchCredentialService(LibraryRepository repository, ICredentialVault vault)
{
    public async Task<SearchSettingsSaveResult> SaveAsync(SearchModel? embedding, SearchModel? reranker, ReadOnlyMemory<byte>? embeddingKey = null,
        ReadOnlyMemory<byte>? rerankerKey = null, bool removeEmbeddingKey = false, bool removeRerankerKey = false, CancellationToken cancellationToken = default)
    {
        if (repository.IsReadOnly) throw new InvalidOperationException("The library is open read-only");
        if (embedding is not null) ConfigurationValidation.ValidateSearchModel(embedding with { KeyRef = null });
        if (reranker is not null) ConfigurationValidation.ValidateSearchModel(reranker with { KeyRef = null });
        var old = await repository.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var fresh = new List<string>();
        AppSettings saved;
        try
        {
            var nextEmbedding = await ConfigureAsync(embedding, old.EmbeddingModel, embeddingKey, removeEmbeddingKey).ConfigureAwait(false);
            var nextReranker = await ConfigureAsync(reranker, old.RerankerModel, rerankerKey, removeRerankerKey).ConfigureAwait(false);
            saved = await repository.SaveSettingsAsync(old with { EmbeddingModel = nextEmbedding, RerankerModel = nextReranker }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (string reference in fresh)
                try { await Task.Run(() => vault.Delete(reference)).ConfigureAwait(false); } catch { }
            throw;
        }
        bool cleanupRequired = false;
        foreach (string? reference in new[] { old.EmbeddingModel?.KeyRef, old.RerankerModel?.KeyRef }.Distinct())
        {
            if (reference is null || reference == saved.EmbeddingModel?.KeyRef || reference == saved.RerankerModel?.KeyRef) continue;
            try { await Task.Run(() => vault.Delete(reference)).ConfigureAwait(false); } catch { cleanupRequired = true; }
        }
        return new(saved, cleanupRequired);

        async Task<SearchModel?> ConfigureAsync(SearchModel? model, SearchModel? previous, ReadOnlyMemory<byte>? replacement, bool remove)
        {
            if (model is null) return null;
            string? reference = remove ? null : previous?.KeyRef;
            if (replacement is { Length: > 0 } secret)
            {
                reference = WindowsCredentialStore.NewReference();
                string target = reference;
                fresh.Add(target);
                await Task.Run(() => vault.Save(target, secret.Span), cancellationToken).ConfigureAwait(false);
            }
            return model with { KeyRef = reference };
        }
    }
}
