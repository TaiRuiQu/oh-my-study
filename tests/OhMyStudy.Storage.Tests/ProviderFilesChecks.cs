using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

internal static partial class Program
{
    private static async Task ProviderFilesChecks()
    {
        string path = Unique("provider-files");
        var original = Fixture<Provider>("provider");
        Check(!original.SupportsFilesApi && !DomainJson.Serialize(original).Contains("supports_files_api", StringComparison.Ordinal), "legacy providers keep Files API disabled without changing their JSON");
        await using (var library = await LibraryRepository.OpenAsync(NewOptions(path)))
            await library.UpsertProviderAsync(original with { SupportsFilesApi = true });
        await using (var library = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite)))
        {
            var provider = (await library.ListProvidersAsync()).Single();
            Check(provider.SupportsFilesApi && provider.KeyRef == original.KeyRef && provider.Models.Count == original.Models.Count, "Files API opt-in persists across library reopen without replacing provider settings");
            await library.UpsertProviderAsync(provider with { SupportsFilesApi = false });
        }
        await using var reopened = await LibraryRepository.OpenAsync(new(path));
        Check(!(await reopened.ListProvidersAsync()).Single().SupportsFilesApi, "Files API can be disabled and reopened read-only");
    }
}
