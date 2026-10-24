using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

internal static partial class Program
{
    private static async Task StudentAnswerStorageChecks()
    {
        string path = Unique("student-page-migration"), backup = Unique("student-page-backup"), restored = Unique("student-page-restored");
        Document original;
        await using (var library = await LibraryRepository.OpenAsync(NewOptions(path)))
        {
            original = await library.ImportDocumentAsync("Blank paper", ImportMode.Blank, [new("exam.png", png), new("reference.png", png, "reference")]);
            original = await library.UpdatePagesAsync(original.Id, original.Pages.Select(page => new PageEdit { Id = page.Id, Rotation = 90 }).ToList());
        }
        using (var database = Db(path))
        {
            string schema = (string)Value(database, "SELECT sql FROM sqlite_master WHERE name='source_pages'")!;
            schema = schema.Replace("\"source_pages\"", "source_pages_v5", StringComparison.Ordinal)
                .Replace("CREATE TABLE source_pages (", "CREATE TABLE source_pages_v5 (", StringComparison.Ordinal)
                .Replace("'material','reference','student'", "'material','reference'", StringComparison.Ordinal);
            Sql(database, schema);
            Sql(database, "INSERT INTO source_pages_v5 SELECT * FROM source_pages; DROP TABLE source_pages; ALTER TABLE source_pages_v5 RENAME TO source_pages; PRAGMA user_version=5;");
        }
        await using (var readOnly = await LibraryRepository.OpenAsync(new(path)))
            Check((await readOnly.InspectAsync()).SchemaVersion == 5 && DomainJson.Serialize(await readOnly.GetDocumentAsync(original.Id)) == DomainJson.Serialize(original), "v5 source pages remain readable without automatic write migration");
        Document appended;
        await using (var library = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite) { MediaProcessor = new FixtureProcessor() }))
        {
            Check((await library.InspectAsync()).SchemaVersion == 6 && DomainJson.Serialize(await library.GetDocumentAsync(original.Id)) == DomainJson.Serialize(original), "v6 migration preserves source page identity, roles, ordering, and rotations");
            appended = await library.AppendDocumentSourcesAsync(original.Id, [new("student.png", png, "student")]);
            Check(appended.Mode == ImportMode.Auto && appended.Pages.Take(2).SequenceEqual(original.Pages) && appended.Pages[2].Role == "student", "student attachment enables grading while preserving original pages");
            await library.VerifyIntegrityAsync();
            await library.BackupAsync(backup);
        }
        await LibraryRepository.RestoreBackupAsync(backup, restored);
        foreach (string directory in new[] { path, restored })
        {
            await using var library = await LibraryRepository.OpenAsync(new(directory));
            Check(DomainJson.Serialize(await library.GetDocumentAsync(original.Id)) == DomainJson.Serialize(appended)
                && (await library.ReadAssetAsync(appended.Pages[2].AssetId)).Bytes.SequenceEqual(png), "student attachment and original bytes survive reopen and backup restore");
        }
    }
}
