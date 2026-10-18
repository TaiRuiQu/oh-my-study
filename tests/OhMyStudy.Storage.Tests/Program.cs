using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Storage;

[assembly: SupportedOSPlatform("windows10.0.19041")]

internal static class Program
{
    private static readonly string Root = Path.Combine(Path.GetTempPath(), "OhMyStudy-Storage-" + Guid.NewGuid().ToString("N"));
    private static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures");
    private static JsonDocument contracts = null!;
    private static JsonDocument assets = null!;
    private static byte[] png = [];
    private static int passed;
    private static T Fixture<T>(string key) => DomainJson.Deserialize<T>(contracts.RootElement.GetProperty(key).GetRawText());
    private static string Unique(string name) => Path.Combine(Root, name + "-" + Guid.NewGuid().ToString("N"));
    private static void Check(bool condition, string name) { if (!condition) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
    private static async Task Fails(Func<Task> action, string name)
    { try { await action(); } catch { Check(true, name); return; } throw new Exception("Expected failure: " + name); }

    public static async Task<int> Main()
    {
        Directory.CreateDirectory(Root);
        try
        {
            contracts = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "Compatibility", "contracts.synthetic.json")));
            assets = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(Fixtures, "Compatibility", "assets.synthetic.json")));
            png = Convert.FromBase64String(assets.RootElement.GetProperty("assets")[0].GetProperty("base64").GetString()!);
            await LegacyMigration();
            await ImportRecovery();
            await PdfRelationships();
            await GradingAndJobs();
            await CredentialAtomicity();
            await CopyUpgradeRecovery();
            Console.WriteLine($"Storage regression: {passed} checks passed. Synthetic temporary libraries only. {Root}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Retained test library: " + Root); return 1; }
        finally { contracts?.Dispose(); assets?.Dispose(); }
    }

    private static LibraryOpenOptions NewOptions(string path) => new(path, LibraryOpenMode.CreateNew) { MediaProcessor = new FixtureProcessor() };
    private static SqliteConnection Db(string root)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(root, "study.sqlite3"), Pooling = false }.ToString());
        connection.Open(); return connection;
    }
    private static void Sql(SqliteConnection db, string sql, params object?[] values)
    {
        using var command = db.CreateCommand(); command.CommandText = sql;
        for (var index = 0; index < values.Length; index++) command.Parameters.AddWithValue("$p" + index, values[index] ?? DBNull.Value);
        command.ExecuteNonQuery();
    }
    private static object? Value(SqliteConnection db, string sql) { using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar(); }

    private static string CreateLegacy(int version, bool migrationConflict = false)
    {
        SQLitePCL.Batteries_V2.Init();
        var path = Unique("旧资料 v" + version); Directory.CreateDirectory(path); Directory.CreateDirectory(Path.Combine(path, "assets"));
        using var db = Db(path);
        Sql(db, File.ReadAllText(Path.Combine(Fixtures, "LegacyDatabase", "001_initial.sql")));
        if (version == 2) Sql(db, File.ReadAllText(Path.Combine(Fixtures, "LegacyDatabase", "002_grading.sql")));
        Sql(db, "PRAGMA user_version=" + version);
        var document = Fixture<Document>("document"); var asset = assets.RootElement.GetProperty("assets")[0];
        File.WriteAllBytes(Path.Combine(path, "assets", asset.GetProperty("filename").GetString()!), png);
        Sql(db, "INSERT INTO assets(id,mime,byte_size,width,height,original_file) VALUES($p0,'image/png',$p1,4,3,$p2)", asset.GetProperty("id").GetString(), png.Length, asset.GetProperty("filename").GetString());
        Sql(db, "INSERT INTO documents(id,name,mode,created_at,pending_pdf_pages) VALUES($p0,$p1,$p2,$p3,0)", document.Id, document.Name, DomainJson.Serialize(document.Mode), document.CreatedAt);
        foreach (var page in document.Pages) Sql(db, "INSERT INTO source_pages(id,document_id,asset_id,page_index,rotation,preview_status,role) VALUES($p0,$p1,$p2,$p3,0,'ready',$p4)", page.Id, document.Id, page.AssetId, page.PageIndex, page.Role);
        Sql(db, "INSERT INTO providers(id,config_json) VALUES($p0,$p1)", Fixture<Provider>("provider").Id, DomainJson.Serialize(Fixture<Provider>("provider")));
        Sql(db, "INSERT INTO settings(id,settings_json) VALUES(1,$p0)", DomainJson.Serialize(Fixture<AppSettings>("settings")));
        if (migrationConflict) Sql(db, "CREATE TABLE questions(synthetic_conflict TEXT)");
        return path;
    }

    private static async Task LegacyMigration()
    {
        foreach (var version in new[] { 1, 2 })
        {
            var path = CreateLegacy(version);
            await using (var read = await LibraryRepository.OpenAsync(new(path)))
            {
                Check((await read.InspectAsync()).SchemaVersion == version, $"v{version} read-only schema unchanged");
                Check(DomainJson.Serialize(await read.GetDocumentAsync(Fixture<Document>("document").Id)) == DomainJson.Serialize(Fixture<Document>("document")), $"v{version} document IDs/order/JSON mode");
                Check((await read.ReadAssetAsync(Fixture<Document>("document").Pages[0].AssetId)).Bytes.SequenceEqual(png), $"v{version} original bytes preserved");
                await Fails(() => read.SaveSettingsAsync(AppSettings.Default), $"v{version} read-only refuses write");
                var backup = Unique("readonly-backup-v" + version); await read.BackupAsync(backup);
                var restored = Unique("readonly-restored-v" + version); await LibraryRepository.RestoreBackupAsync(backup, restored);
                await using var copy = await LibraryRepository.OpenAsync(new(restored));
                Check((await copy.InspectAsync()).SchemaVersion == version && (await copy.GetDocumentAsync(Fixture<Document>("document").Id)).Pages.Count == 2,
                    $"v{version} read-only online backup/restore without source upgrade");
            }
            await using (var repository = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite)))
            {
                Check((await repository.InspectAsync()).SchemaVersion == 2, $"v{version} upgraded/opened v2");
                Check((await repository.ListProvidersAsync()).Single().Id == Fixture<Provider>("provider").Id, "provider stable reference");
                var document = await repository.GetDocumentAsync(Fixture<Document>("document").Id);
                var reordered = await repository.UpdatePagesAsync(document.Id, document.Pages.AsEnumerable().Reverse().Select(p => new PageEdit { Id = p.Id, Rotation = 90 }).ToList());
                Check(reordered.Pages[0].Id == document.Pages[1].Id && reordered.Pages.All(p => p.Rotation == 90), "atomic reorder/rotation");
                await Fails(() => LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite)), "cooperating writer lease");
                await repository.VerifyIntegrityAsync();
            }
            if (version == 1) Check(Directory.GetFiles(path, "study.sqlite3.pre-v2-*").Length == 1, "v1 consistent preupgrade database backup");
        }
        var failed = CreateLegacy(1, true);
        await Fails(() => LibraryRepository.OpenAsync(new(failed, LibraryOpenMode.ReadWrite)), "migration conflict rejects open");
        using var broken = Db(failed);
        Check(Convert.ToInt32(Value(broken, "PRAGMA user_version")) == 1 && Convert.ToInt32(Value(broken, "SELECT COUNT(*) FROM pragma_table_info('source_pages') WHERE name='original_asset_id'")) == 0, "migration rollback includes ALTER and user_version");
        var future = CreateLegacy(2); using (var db = Db(future)) Sql(db, "PRAGMA user_version=99");
        await Fails(() => LibraryRepository.OpenAsync(new(future, LibraryOpenMode.ReadWrite)), "future schema rejects open");
    }

    private static async Task CopyUpgradeRecovery()
    {
        foreach (int version in new[] { 1, 2 })
        {
            string source = CreateLegacy(version), backup = Unique("cutover-backup"), destination = Unique("cutover-copy");
            byte[] originalDatabase = File.ReadAllBytes(Path.Combine(source, "study.sqlite3"));
            var migration = await LibraryMigrationService.CopyAndUpgradeAsync(source, backup, destination);
            Check(migration.OriginalSchemaVersion == version && migration.CurrentSchemaVersion == 2 && migration.Documents == 1, $"v{version} copy-only migration upgrades verified destination");
            Check(File.ReadAllBytes(Path.Combine(source, "study.sqlite3")).SequenceEqual(originalDatabase), $"v{version} source database bytes untouched");
            Check(migration.CredentialReferences == 1 && migration.ContentSha256.Length == 64, "cutover validates credential references without reading secrets");
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(destination, "migration-receipt.json")));
            Check(!receipt.RootElement.GetProperty("credentialsReadOrExported").GetBoolean() && !receipt.RootElement.GetProperty("activeLibraryChanged").GetBoolean(), "durable receipt separates copy validation from user cutover");
            string rollback = Unique("cutover-rollback"); await LibraryRepository.RestoreBackupAsync(backup, rollback);
            await using var recovered = await LibraryRepository.OpenAsync(new(rollback));
            Check((await recovered.InspectAsync()).SchemaVersion == version && (await recovered.ListProvidersAsync()).Single().KeyRef == Fixture<Provider>("provider").KeyRef, "rollback preserves original schema and key reference");
            await Fails(() => LibraryMigrationService.CopyAndUpgradeAsync(source, Unique("unused-backup"), destination), "migration refuses overwrite before creating backup");
            await Fails(() => LibraryMigrationService.CopyAndUpgradeAsync(source, Path.Combine(source, "inside"), Unique("unused-copy")), "migration rejects backup inside original");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            string canceledCopy = Unique("canceled-copy"), canceledBackup = Unique("canceled-backup");
            await Fails(() => LibraryMigrationService.CopyAndUpgradeAsync(source, canceledBackup, canceledCopy, cancelled.Token), "canceled migration creates no destination");
            Check(!Directory.Exists(canceledBackup) && !Directory.Exists(canceledCopy), "pre-cancel leaves source and destinations unchanged");
        }
    }

    private static async Task ImportRecovery()
    {
        var path = Unique("import"); await using var repository = await LibraryRepository.OpenAsync(NewOptions(path));
        var document = await repository.ImportDocumentAsync("资料 ' 中文", ImportMode.AiGrading, [new("fake.pdf", png), new("reference", png, "reference")]);
        Check(document.Pages.Count == 2 && document.Pages[1].Role == "reference", "magic drives import, role retained");
        Check((await repository.ReadAssetAsync(document.Pages[0].AssetId)).Bytes.SequenceEqual(png), "import original hash unchanged");
        var second = await repository.ImportDocumentAsync("duplicate", ImportMode.Blank, [new("fake.png", png)]);
        Check(second.Pages[0].AssetId != document.Pages[0].AssetId, "duplicate imports independent IDs");
        using (var db = Db(path)) Sql(db, "CREATE TRIGGER fail_assets BEFORE INSERT ON assets BEGIN SELECT RAISE(ABORT,'synthetic disk/SQL failure'); END");
        var count = Directory.GetFiles(Path.Combine(path, "assets")).Length;
        await Fails(() => repository.ImportDocumentAsync("rollback", ImportMode.Blank, [new("", png)]), "asset insert failure");
        Check((await repository.ListDocumentsAsync()).Count == 2 && Directory.GetFiles(Path.Combine(path, "assets")).Length == count, "asset files and database rollback together");
        using (var db = Db(path)) Sql(db, "DROP TRIGGER fail_assets");
        var backup = Unique("backup"); var result = await repository.BackupAsync(backup);
        Check(result.AssetFileCount == count && new FileInfo(Path.Combine(path, "study.sqlite3-wal")).Length > 0, "online backup while WAL has committed rows");
        var restored = Unique("restored"); await LibraryRepository.RestoreBackupAsync(backup, restored);
        await using (var restore = await LibraryRepository.OpenAsync(new(restored)))
        {
            Check((await restore.ListDocumentsAsync()).Count == 2, "WAL backup restores committed documents");
            Check((await restore.ReadAssetAsync(document.Pages[0].AssetId)).Bytes.SequenceEqual(png), "backup asset SHA256 roundtrip");
        }
        await Fails(() => LibraryRepository.RestoreBackupAsync(backup, restored), "restore refuses existing directory");
        var corrupted = Directory.GetFiles(Path.Combine(backup, "assets"))[0]; File.AppendAllText(corrupted, "corrupt");
        var rejected = Unique("rejected"); await Fails(() => LibraryRepository.RestoreBackupAsync(backup, rejected), "restore rejects asset corruption");
        Check(!Directory.Exists(rejected), "failed restore leaves destination absent");
        using (var db = Db(path)) Sql(db, "UPDATE assets SET original_file='../escape.png' WHERE id=$p0", document.Pages[0].AssetId);
        await Fails(() => repository.ReadAssetAsync(document.Pages[0].AssetId), "asset traversal rejected");
        using (var db = Db(path)) Sql(db, "UPDATE assets SET original_file=$p0 WHERE id=$p1", document.Pages[0].AssetId + ".png", document.Pages[0].AssetId);
        var orphan = Path.Combine(path, "assets", Guid.NewGuid().ToString("D") + ".png"); File.WriteAllBytes(orphan, png);
        await repository.RecoverOrphanedAssetsAsync(true); Check(!File.Exists(orphan), "crash orphan cleanup");
        using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Fails(() => repository.ImportDocumentAsync("canceled", ImportMode.Blank, [new("", png)], canceled.Token), "pre-canceled import refused");
        var settings = Enumerable.Range(0, 32).Select(index => repository.SaveSettingsAsync(AppSettings.Default with { DefaultModel = index.ToString() })).ToArray();
        await Task.WhenAll(settings); Check((await repository.GetSettingsAsync()).DefaultModel == "31", "concurrent queued writes commit in order");
        using (var blocker = Db(path))
        using (var duringCommit = new CancellationTokenSource())
        {
            Sql(blocker, "BEGIN IMMEDIATE");
            var pending = repository.SaveSettingsAsync(AppSettings.Default with { DefaultModel = "must rollback" }, duringCommit.Token);
            duringCommit.Cancel(); Sql(blocker, "COMMIT");
            await Fails(() => pending, "cancellation before commit aborts queued write");
            Check((await repository.GetSettingsAsync()).DefaultModel == "31", "canceled transaction does not commit");
        }
        var empty = Unique("path-failure"); await using var failureRepo = await LibraryRepository.OpenAsync(NewOptions(empty));
        Directory.Delete(Path.Combine(empty, "assets")); File.WriteAllText(Path.Combine(empty, "assets"), "occupied");
        await Fails(() => failureRepo.ImportDocumentAsync("cannot write", ImportMode.Blank, [new("", png)]), "real filesystem path write failure");
        Check((await failureRepo.ListDocumentsAsync()).Count == 0, "path failure leaves no document");
    }

    private static JobEvent Event(Job job) => new() { Id = Guid.NewGuid().ToString("D"), JobId = job.Id, Sequence = job.Sequence, CreatedAt = job.UpdatedAt, Kind = "snapshot", Payload = DomainJson.ToElement(job) };

    private static async Task PdfRelationships()
    {
        var pdf = Convert.FromBase64String(assets.RootElement.GetProperty("assets")[1].GetProperty("base64").GetString()!);
        var path = Unique("pdf-import"); await using (var repository = await LibraryRepository.OpenAsync(NewOptions(path)))
        {
            var document = await repository.ImportDocumentAsync("synthetic PDF", ImportMode.Blank, [new("fake.jpg", pdf)]);
            Check(document.Pages.Count == 2 && document.Pages[0].PdfPageIndex == 0 && document.Pages[1].PdfPageIndex == 1 && document.Pages.All(p => p.OriginalAssetId == document.Pages[0].OriginalAssetId), "PDF original/page relationship and ordinals");
            Check((await repository.ReadAssetAsync(document.Pages[0].OriginalAssetId!)).Bytes.SequenceEqual(pdf), "PDF original bytes untouched");
        }
        var old = CreateLegacy(1); var template = Fixture<Document>("document"); var asset = template.Pages[0].AssetId;
        using (var db = Db(old))
        {
            Sql(db, "DELETE FROM source_pages WHERE id=$p0", template.Pages[1].Id);
            Sql(db, "UPDATE documents SET pending_pdf_pages=1");
            Sql(db, "UPDATE source_pages SET preview_status='pending_pdf',rotation=90");
            Sql(db, "UPDATE assets SET mime='application/pdf',byte_size=$p0,width=NULL,height=NULL,original_file=$p1", pdf.Length, asset + ".pdf");
        }
        File.WriteAllBytes(Path.Combine(old, "assets", asset + ".pdf"), pdf);
        await using var upgraded = await LibraryRepository.OpenAsync(new(old, LibraryOpenMode.ReadWrite) { MediaProcessor = new FixtureProcessor() });
        var parsed = await upgraded.ParsePendingPdfsAsync(template.Id);
        Check(!parsed.PendingPdfPages && parsed.Pages.Count == 2 && parsed.Pages[0].Id == template.Pages[0].Id && parsed.Pages.All(p => p.Rotation == 90), "pending PDF preserves first page ID and rotation");
        Check(parsed.Pages.All(p => p.OriginalAssetId == asset) && (await upgraded.ReadAssetAsync(asset)).Bytes.SequenceEqual(pdf), "pending PDF preserves old original asset ID/bytes");
    }
    private static async Task GradingAndJobs()
    {
        var path = CreateLegacy(2); await using var repository = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite));
        var job = Fixture<Job>("job") with { State = JobState.Running, Sequence = 1, Progress = 0 };
        await repository.SaveJobEventAsync(job, Event(job)); await repository.BeginGradingAsync(job);
        await repository.SaveGradingReferencesAsync(job.Id, Fixture<List<ReferenceAnswer>>("references"));
        var question = Fixture<Question>("question"); var saved = await repository.SaveGradingQuestionAsync(job.Id, question);
        Check((await repository.SaveGradingQuestionAsync(job.Id, question)).Id == saved.Id, "AI upsert stable ID/idempotent row");
        var viewed = job.Input!.Document.Pages.Select(p => p.Id).ToHashSet();
        await repository.FinishGradingAsync(job.Id, Fixture<List<Coverage>>("coverage"), viewed, false, new HashSet<string> { question.DraftKey });
        await repository.RecordModelCallAsync(job.Id, 0, contracts.RootElement.GetProperty("model_call"));
        Check((await repository.GradingResultAsync(job.Input.Document.Id)).Coverage.Count == 2, "coverage and reference/modelcall persisted");
        var canceled = job with { State = JobState.Cancelled, Sequence = 2, Progress = 25 };
        await repository.SaveJobEventAsync(canceled, Event(canceled));
        await Fails(() => repository.SaveGradingQuestionAsync(job.Id, question), "late result after cancellation refused");
        Check((await repository.GradingQuestionsAsync(job.Input.Document.Id)).Single().Id == saved.Id, "cancel retains successful previous save");
        await Fails(() => repository.SaveJobEventAsync(canceled, Event(canceled)), "event sequence reuse refused atomically");
        Check((await repository.ListJobEventsAsync(job.Id)).Count == 2 && (await repository.LoadJobSnapshotsAsync()).Single().State == JobState.Cancelled, "snapshot/events transaction and order");
        var edited = await repository.EditQuestionAsync(saved.Id, question);
        Check(edited.Question.Attempt!.Grade.Source == GradeSource.Manual, "manual edit marks provenance");
        var rerun = job with { Id = Guid.NewGuid().ToString("D"), Sequence = 1, CreatedAt = job.CreatedAt + 1 };
        await repository.SaveJobEventAsync(rerun, Event(rerun)); await repository.BeginGradingAsync(rerun);
        var updated = await repository.SaveGradingQuestionAsync(rerun.Id, question);
        Check(updated.Id == saved.Id && updated.Question.Attempt!.Grade.Source == GradeSource.Manual, "regrade preserves manual grade");
        await Fails(() => repository.SaveGradingQuestionAsync(rerun.Id, question with { Attempt = null }), "regrade cannot remove protected attempt");
        await Fails(() => repository.UpdatePagesAsync(job.Input.Document.Id, []), "active run blocks page edit");
    }

    private static async Task CredentialAtomicity()
    {
        var path = Unique("credentials"); await using var repository = await LibraryRepository.OpenAsync(NewOptions(path));
        var vault = new MemoryVault(); var service = new ProviderCredentialService(repository, vault);
        var provider = Fixture<Provider>("provider"); var first = await service.SaveAsync(provider, Encoding.UTF8.GetBytes("synthetic-a"));
        var reference = first.Provider.KeyRef!;
        var reused = await service.SaveAsync(provider with { Name = "renamed" });
        Check(reused.Provider.KeyRef == reference && reused.Provider.HasKey, "credential reference reused without replacement");
        vault.FailSave = true;
        await Fails(() => service.SaveAsync(provider, Encoding.UTF8.GetBytes("synthetic-b")), "vault save failure rejects provider change");
        Check((await repository.ListProvidersAsync()).Single().KeyRef == reference && vault.Values.ContainsKey(reference), "failed replacement retains original credential/reference");
        vault.FailSave = false;
        using (var db = Db(path)) Sql(db, "CREATE TRIGGER fail_provider BEFORE UPDATE ON providers BEGIN SELECT RAISE(ABORT,'synthetic'); END");
        await Fails(() => service.SaveAsync(provider, Encoding.UTF8.GetBytes("synthetic-c")), "provider SQL failure after vault save");
        Check(vault.Values.Count == 1 && vault.Values.ContainsKey(reference), "failed SQL cleans new vault key and retains old");
        using (var db = Db(path)) Sql(db, "DROP TRIGGER fail_provider");
        var replacement = await service.SaveAsync(provider, Encoding.UTF8.GetBytes("synthetic-d"));
        Check(replacement.Provider.KeyRef != reference && !vault.Values.ContainsKey(reference), "replacement commits new reference then deletes old");
        Check(!await service.DeleteAsync(provider.Id) && vault.Values.Count == 0 && (await repository.ListProvidersAsync()).Count == 0, "provider and synthetic credential deletion");
    }

    private sealed class FixtureProcessor : ILibraryMediaProcessor
    {
        public Task<PreparedMedia> PrepareAsync(ImportSource source, int maximumPages, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (source.Bytes.AsSpan().StartsWith("%PDF-"u8))
            {
                if (maximumPages < 2) throw new InvalidDataException("page limit");
                // These images are storage fixture stand-ins, never presented as PDF rendering evidence.
                return Task.FromResult(new PreparedMedia("application/pdf", null, null, null, [new(png.ToArray(), 4, 3, png.ToArray()), new(png.ToArray(), 4, 3, png.ToArray())]));
            }
            if (!source.Bytes.SequenceEqual(png)) throw new InvalidDataException("Synthetic processor accepts only fixture PNG");
            return Task.FromResult(new PreparedMedia("image/png", 4, 3, png.ToArray()));
        }
    }
    private sealed class MemoryVault : ICredentialVault
    {
        public Dictionary<string, byte[]> Values { get; } = [];
        public bool FailSave { get; set; }
        public void Save(string reference, ReadOnlySpan<byte> secret) { if (FailSave) throw new IOException("synthetic vault failure"); Values[reference] = secret.ToArray(); }
        public byte[]? Read(string reference) => Values.TryGetValue(reference, out var value) ? value.ToArray() : null;
        public void Delete(string reference) { if (Values.Remove(reference, out var secret)) CryptographicOperations.ZeroMemory(secret); }
    }
}
