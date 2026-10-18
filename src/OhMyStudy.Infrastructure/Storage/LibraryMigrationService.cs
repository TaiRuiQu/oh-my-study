using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed record LibraryMigrationResult(string BackupDirectory, string LibraryDirectory, int OriginalSchemaVersion,
    int CurrentSchemaVersion, int Documents, int Questions, int CredentialReferences, string ContentSha256);

/// <summary>Migration changes only a new verified copy. A failed operation retains the original,
/// the completed backup and any restored copy for recovery; it never changes the active-library pointer.</summary>
public static class LibraryMigrationService
{
    public static async Task<LibraryMigrationResult> CopyAndUpgradeAsync(string sourceDirectory, string backupDirectory,
        string newLibraryDirectory, CancellationToken cancellationToken = default)
    {
        string source = Path.TrimEndingDirectorySeparator(Path.GetFullPath(sourceDirectory)), backup = Path.TrimEndingDirectorySeparator(Path.GetFullPath(backupDirectory)), destination = Path.TrimEndingDirectorySeparator(Path.GetFullPath(newLibraryDirectory));
        foreach (string path in new[] { backup, destination })
            if (path.Equals(source, StringComparison.OrdinalIgnoreCase) || path.StartsWith(source + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new IOException("请为备份和新资料选择独立目录。");
        if (backup.Equals(destination, StringComparison.OrdinalIgnoreCase) || backup.StartsWith(destination + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || destination.StartsWith(backup + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new IOException("备份与新资料不能互相包含。");
        if (Directory.Exists(destination) || File.Exists(destination)) throw new IOException("新资料位置已存在。");
        await using (var original = await LibraryRepository.OpenAsync(new(source), cancellationToken).ConfigureAwait(false))
        {
            await original.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
            await original.BackupAsync(backup, cancellationToken).ConfigureAwait(false);
        }
        // Read the stable backup, rather than taking unrelated live SELECTs as a snapshot.
        Snapshot expected;
        int originalVersion;
        await using (var saved = await LibraryRepository.OpenAsync(new(backup), cancellationToken).ConfigureAwait(false))
        {
            originalVersion = (await saved.InspectAsync(cancellationToken).ConfigureAwait(false)).SchemaVersion;
            expected = await FingerprintAsync(saved, cancellationToken).ConfigureAwait(false);
        }
        await LibraryRepository.RestoreBackupAsync(backup, destination, cancellationToken).ConfigureAwait(false);
        int currentVersion;
        await using (var copy = await LibraryRepository.OpenAsync(new(destination, LibraryOpenMode.ReadWrite), cancellationToken).ConfigureAwait(false))
        {
            await copy.VerifyIntegrityAsync(cancellationToken).ConfigureAwait(false);
            currentVersion = (await copy.InspectAsync(cancellationToken).ConfigureAwait(false)).SchemaVersion;
            var actual = await FingerprintAsync(copy, cancellationToken).ConfigureAwait(false);
            if (actual != expected) throw new InvalidDataException("新资料校验失败。请保留原资料与备份，重新选择新目录恢复。");
        }
        cancellationToken.ThrowIfCancellationRequested();
        var result = new LibraryMigrationResult(backup, destination, originalVersion, currentVersion, expected.Documents,
            expected.Questions, expected.CredentialReferences, expected.Digest);
        string receipt = Path.Combine(destination, "migration-receipt.json");
        using (var stream = new FileStream(receipt, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        {
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject(); writer.WriteNumber("format", 1); writer.WriteString("timestampUtc", DateTimeOffset.UtcNow);
            writer.WriteNumber("originalSchemaVersion", originalVersion); writer.WriteNumber("currentSchemaVersion", currentVersion);
            writer.WriteNumber("documents", expected.Documents); writer.WriteNumber("questions", expected.Questions);
            writer.WriteNumber("credentialReferences", expected.CredentialReferences); writer.WriteString("contentSha256", expected.Digest);
            writer.WriteBoolean("credentialsReadOrExported", false); writer.WriteBoolean("activeLibraryChanged", false);
            writer.WriteEndObject(); writer.Flush(); stream.Flush(true);
        }
        return result;
    }

    private sealed record Snapshot(string Digest, int Documents, int Questions, int CredentialReferences);
    private static async Task<Snapshot> FingerprintAsync(LibraryRepository library, CancellationToken ct)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        void Add(string text)
        {
            byte[] bytes = Encoding.UTF8.GetBytes(text);
            hash.AppendData(BitConverter.GetBytes(bytes.Length)); hash.AppendData(bytes);
        }
        var documents = await library.ListDocumentsAsync(ct).ConfigureAwait(false);
        var questions = await library.ListQuestionsAsync(ct).ConfigureAwait(false);
        var providers = await library.ListProvidersAsync(ct).ConfigureAwait(false);
        Add(await library.MigrationContentFingerprintAsync(ct).ConfigureAwait(false));
        Add(DomainJson.Serialize(await library.GetSettingsAsync(ct).ConfigureAwait(false)));
        foreach (var item in providers.OrderBy(p => p.Id, StringComparer.Ordinal)) Add(DomainJson.Serialize(item));
        foreach (var document in documents.OrderBy(d => d.Id, StringComparer.Ordinal))
        {
            Add(DomainJson.Serialize(document));
            var result = await library.GradingResultAsync(document.Id, ct).ConfigureAwait(false);
            foreach (var reference in result.References) Add(DomainJson.Serialize(reference));
            foreach (var coverage in result.Coverage) Add(DomainJson.Serialize(coverage));
            foreach (var call in result.ModelCalls) Add(call.GetRawText());
        }
        foreach (var question in questions.OrderBy(q => q.Id, StringComparer.Ordinal)) Add(DomainJson.Serialize(question));
        foreach (var job in (await library.LoadJobSnapshotsAsync(ct).ConfigureAwait(false)).OrderBy(j => j.Id, StringComparer.Ordinal))
        {
            Add(DomainJson.Serialize(job));
            foreach (var item in await library.ListJobEventsAsync(job.Id, cancellationToken: ct).ConfigureAwait(false)) Add(DomainJson.Serialize(item));
        }
        return new(Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant(), documents.Count, questions.Count, providers.Count(p => p.KeyRef is not null));
    }
}
