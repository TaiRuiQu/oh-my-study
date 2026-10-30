using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Credentials;

public sealed class WorkflowCredentialService(LibraryRepository repository, ICredentialVault vault)
{
    public async Task<AppSettings> SaveAsync(ExamWorkflowSettings workflow, ReadOnlyMemory<byte>? minerUToken, ReadOnlyMemory<byte>? splitterKey,
        CancellationToken cancellationToken)
    {
        ConfigurationValidation.ValidateWorkflow(workflow);
        var current = await repository.GetSettingsAsync(cancellationToken).ConfigureAwait(false);
        var fresh = new List<string>();
        try
        {
            string? tokenRef = await ConfigureAsync(current.ExamWorkflow?.MinerUTokenRef, minerUToken).ConfigureAwait(false);
            string? splitRef = workflow.SplitterModel is not null ? workflow.Splitter.KeyRef
                : await ConfigureAsync(current.ExamWorkflow?.Splitter.KeyRef, splitterKey).ConfigureAwait(false);
            return await repository.SaveSettingsAsync(current with { ExamWorkflow = workflow with { MinerUTokenRef = tokenRef,
                Splitter = workflow.Splitter with { KeyRef = splitRef } } }, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            foreach (var reference in fresh)
                try { vault.Delete(reference); } catch { }
            throw;
        }

        async Task<string?> ConfigureAsync(string? previous, ReadOnlyMemory<byte>? replacement)
        {
            if (replacement is not { Length: > 0 } bytes) return previous;
            string reference = WindowsCredentialStore.NewReference();
            fresh.Add(reference);
            await Task.Run(() => vault.Save(reference, bytes.Span), cancellationToken).ConfigureAwait(false);
            return reference;
        }
    }
}
