using System.Security.Cryptography;
using System.Runtime.CompilerServices;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Credentials;

public interface ICredentialVault
{
    void Save(string reference, ReadOnlySpan<byte> secret);
    byte[]? Read(string reference);
    void Delete(string reference);
}

public sealed class WindowsCredentialVault : ICredentialVault
{
    public void Save(string reference, ReadOnlySpan<byte> secret) => WindowsCredentialStore.Save(reference, secret);
    public byte[]? Read(string reference) => WindowsCredentialStore.Read(reference);
    public void Delete(string reference) => WindowsCredentialStore.Delete(reference);
}

public sealed record ProviderSaveResult(Provider Provider, bool CredentialCleanupRequired);

/// <summary>New secrets get a new target. A failed database commit leaves the old target intact.
/// SQLite and Credential Manager cannot form a distributed transaction: an abrupt process loss
/// can leave an unreferenced credential, but cannot destroy the previously referenced secret.</summary>
public sealed class ProviderCredentialService(LibraryRepository repository, ICredentialVault vault)
{
    private static readonly ConditionalWeakTable<LibraryRepository, SemaphoreSlim> Gates = new();
    private readonly SemaphoreSlim mutation = Gates.GetValue(repository, _ => new(1, 1));

    public async Task<ProviderSaveResult> SaveAsync(Provider provider, ReadOnlyMemory<byte>? replacementSecret = null, CancellationToken cancellationToken = default)
    {
        if (repository.IsReadOnly) throw new InvalidOperationException("资料库以只读方式打开");
        ConfigurationValidation.ValidateProvider(provider with { KeyRef = null });
        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        var secret = replacementSecret is { Length: > 0 } bytes ? bytes.ToArray() : null;
        string? fresh = null;
        try
        {
            var old = (await repository.ListProvidersAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(p => p.Id == provider.Id)?.KeyRef;
            var stored = provider with { KeyRef = old, HasKey = false };
            if (secret is not null)
            {
                fresh = WindowsCredentialStore.NewReference();
                var reference = fresh;
                await Task.Run(() => vault.Save(reference, secret), cancellationToken).ConfigureAwait(false);
                stored = stored with { KeyRef = fresh };
            }
            Provider saved;
            try { saved = await repository.UpsertProviderAsync(stored, cancellationToken).ConfigureAwait(false); }
            catch (Exception commitError)
            {
                if (fresh is not null)
                {
                    try { await Task.Run(() => vault.Delete(fresh)).ConfigureAwait(false); }
                    catch (Exception cleanupError) { throw new AggregateException("供应商未保存，新增凭据清理失败", commitError, cleanupError); }
                }
                throw;
            }
            var cleanupRequired = false;
            if (fresh is not null && old is not null)
            {
                try { await Task.Run(() => vault.Delete(old)).ConfigureAwait(false); }
                catch { cleanupRequired = true; }
            }
            // Commit succeeded. Do not convert a subsequent cancellation or status-query failure
            // into a false report that the provider was not saved.
            var hasKey = false;
            if (saved.KeyRef is { } target)
            {
                try
                {
                    var storedSecret = await Task.Run(() => vault.Read(target)).ConfigureAwait(false);
                    hasKey = storedSecret is not null;
                    if (storedSecret is not null) CryptographicOperations.ZeroMemory(storedSecret);
                }
                catch { }
            }
            return new(saved with { HasKey = hasKey }, cleanupRequired);
        }
        finally { if (secret is not null) CryptographicOperations.ZeroMemory(secret); mutation.Release(); }
    }

    public async Task<bool> DeleteAsync(string providerId, CancellationToken cancellationToken = default)
    {
        if (repository.IsReadOnly) throw new InvalidOperationException("资料库以只读方式打开");
        await mutation.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var reference = (await repository.ListProvidersAsync(cancellationToken).ConfigureAwait(false)).SingleOrDefault(p => p.Id == providerId)?.KeyRef;
            await repository.DeleteProviderAsync(providerId, cancellationToken).ConfigureAwait(false);
            if (reference is null) return false;
            try { await Task.Run(() => vault.Delete(reference)).ConfigureAwait(false); return false; }
            catch { return true; } // Saved deletion remains true; caller records cleanup as a separate outcome.
        }
        finally { mutation.Release(); }
    }
}
