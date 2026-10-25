using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

internal static partial class Program
{
    private static async Task ProviderFilesChecks()
    {
        string path = Unique("provider-files");
        var original = Fixture<Provider>("provider");
        var legacy = DomainJson.Serialize(original);
        Check(!original.SupportsFilesApi && !original.CodexMode && !legacy.Contains("supports_files_api", StringComparison.Ordinal) && !legacy.Contains("codex_mode", StringComparison.Ordinal), "legacy providers keep Files API and Codex mode disabled without changing their JSON");
        await using (var library = await LibraryRepository.OpenAsync(NewOptions(path)))
            await library.UpsertProviderAsync(original with { SupportsFilesApi = true, CodexMode = true });
        await using (var library = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite)))
        {
            var provider = (await library.ListProvidersAsync()).Single();
            Check(provider.SupportsFilesApi && provider.CodexMode && provider.KeyRef == original.KeyRef && provider.Models.Count == original.Models.Count, "Files API and Codex mode opt-in persists across library reopen without replacing provider settings");
            await library.UpsertProviderAsync(provider with { SupportsFilesApi = false, CodexMode = false });
        }
        await using var reopened = await LibraryRepository.OpenAsync(new(path));
        var disabled = (await reopened.ListProvidersAsync()).Single();
        Check(!disabled.SupportsFilesApi && !disabled.CodexMode && !DomainJson.Serialize(disabled).Contains("codex_mode", StringComparison.Ordinal), "Files API and Codex mode can be disabled and reopened read-only");
    }
}
