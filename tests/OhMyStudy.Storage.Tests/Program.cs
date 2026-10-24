using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Storage;

[assembly: SupportedOSPlatform("windows10.0.19041")]

internal static partial class Program
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
            await QuestionAssetMigration();
            await ImportRecovery();
            await ManagedLibraryLocation();
            await DirectoryPromotionLocks();
            await PdfRelationships();
            await GradingAndJobs();
            await JobRetryChecks();
            await ProviderFilesChecks();
            await PaperMetadata();
            await StudentAnswerStorageChecks();
            await RetiredProviders();
            await CredentialAtomicity();
            await CopyUpgradeRecovery();
            await ComplexCopyUpgradeRecovery();
            await QuestionBankScale();
            await SemanticSearchChecks();
            await DiagnosticChecks();
            Console.WriteLine($"Storage regression: {passed} checks passed. Synthetic temporary libraries only. {Root}");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); Console.Error.WriteLine("Retained test library: " + Root); return 1; }
        finally { contracts?.Dispose(); assets?.Dispose(); }
    }

    private static LibraryOpenOptions NewOptions(string path) => new(path, LibraryOpenMode.CreateNew) { MediaProcessor = new FixtureProcessor() };

    private static async Task PaperMetadata()
    {
        string root = Unique("paper-info"), backup = Unique("paper-info-backup"), copy = Unique("paper-info-copy");
        string id;
        await using (var library = await LibraryRepository.OpenAsync(NewOptions(root)))
        {
            var paper = await library.ImportDocumentAsync("原始标题", ImportMode.TeacherGraded, [new("paper.png", png)]); id = paper.Id;
            await library.UpdatePaperAsync(id, "整理后的标题", "数学", 86.5, 100);
            await Fails(() => library.UpdatePaperAsync(id, "错误标题", "数学", double.NaN, 100), "nonfinite paper score rejected");
            var row = (await library.ListPapersAsync()).Single();
            Check(row.Document.Name == "整理后的标题" && row.Subject == "数学" && row.Earned == 86.5 && row.Possible == 100 && row.Document.Pages.Count == 0, "paper metadata stored and lightweight rows omit source pages");
            await library.BackupAsync(backup);
        }
        await LibraryRepository.RestoreBackupAsync(backup, copy);
        foreach (string path in new[] { root, copy })
        {
            await using var reopened = await LibraryRepository.OpenAsync(new(path)); var row = (await reopened.ListPapersAsync()).Single();
            Check(row.Document.Id == id && row.Subject == "数学" && row.Earned == 86.5 && row.Possible == 100, "paper metadata survives reopen and backup restore");
        }
    }

    private static async Task RetiredProviders()
    {
        var path = Unique("retired-provider");
        var provider = Fixture<Provider>("provider");
        await using (var library = await LibraryRepository.OpenAsync(NewOptions(path)))
        {
            await library.UpsertProviderAsync(provider);
            var retiredId = Guid.NewGuid().ToString();
            var retired = JsonSerializer.SerializeToNode(provider, DomainJson.Context.Provider)!;
            retired["id"] = retiredId;
            retired["protocol"] = "gemini";
            retired["models"] = System.Text.Json.Nodes.JsonNode.Parse("""[{"id":"retired","capabilities":{"thinking":[{"kind":"gemini_budget","tokens":-1}]}}]""");
            var raw = retired.ToJsonString();
            using (var db = Db(path)) Sql(db, "INSERT INTO providers(id,config_json) VALUES($p0,$p1)", retiredId, raw);
            Check((await library.ListProvidersAsync()).Select(p => p.Id).SequenceEqual([provider.Id]), "retired protocol hidden without blocking supported providers");
            using (var db = Db(path))
            {
                using var command = db.CreateCommand();
                command.CommandText = "SELECT config_json FROM providers WHERE id=$p0";
                command.Parameters.AddWithValue("$p0", retiredId);
                Check((string?)command.ExecuteScalar() == raw, "retired provider record preserved exactly");
            }
            await Fails(() => library.UpsertProviderAsync(provider with { Protocol = (Protocol)2 }), "removed protocol cannot be saved");
        }
        await using var reopened = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite));
        Check((await reopened.ListProvidersAsync()).Count == 1, "library with retired protocol reopens");
    }
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
                Check((await repository.InspectAsync()).SchemaVersion == 6, $"v{version} upgraded/opened v6");
                Check((await repository.ListProvidersAsync()).Single().Id == Fixture<Provider>("provider").Id, "provider stable reference");
                var document = await repository.GetDocumentAsync(Fixture<Document>("document").Id);
                var reordered = await repository.UpdatePagesAsync(document.Id, document.Pages.AsEnumerable().Reverse().Select(p => new PageEdit { Id = p.Id, Rotation = 90 }).ToList());
                Check(reordered.Pages[0].Id == document.Pages[1].Id && reordered.Pages.All(p => p.Rotation == 90), "atomic reorder/rotation");
                await Fails(() => LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite)), "cooperating writer lease");
                await repository.VerifyIntegrityAsync();
            }
            if (version == 1) Check(Directory.GetFiles(path, "study.sqlite3.pre-v6-*").Length == 1, "v1 consistent preupgrade database backup");
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
            Check(migration.OriginalSchemaVersion == version && migration.CurrentSchemaVersion == 6 && migration.Documents == 1, $"v{version} copy-only migration upgrades verified destination");
            Check(File.ReadAllBytes(Path.Combine(source, "study.sqlite3")).SequenceEqual(originalDatabase), $"v{version} source database bytes untouched");
            Check(migration.CredentialReferences == 1 && migration.ContentSha256.Length == 64, "cutover validates credential references without reading secrets");
            using var receipt = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(destination, "migration-receipt.json")));
            Check(!receipt.RootElement.GetProperty("credentialsReadOrExported").GetBoolean() && !receipt.RootElement.GetProperty("activeLibraryChanged").GetBoolean(), "durable receipt separates copy validation from user cutover");
            string rollback = Unique("cutover-rollback"); await LibraryRepository.RestoreBackupAsync(backup, rollback);
            await using var recovered = await LibraryRepository.OpenAsync(new(rollback));
            Check((await recovered.InspectAsync()).SchemaVersion == version && (await recovered.ListProvidersAsync()).Single().KeyRef == Fixture<Provider>("provider").KeyRef, "rollback preserves original schema and key reference");
            await Fails(() => LibraryMigrationService.CopyAndUpgradeAsync(source, Unique("unused-backup"), destination), "migration refuses overwrite before creating backup");
            await Fails(() => LibraryMigrationService.CopyAndUpgradeAsync(source, Path.Combine(source, "inside"), Unique("unused-copy")), "migration rejects backup inside original");
            await Fails(() => LibraryMigrationService.CopyAndUpgradeAsync(source + Path.DirectorySeparatorChar, Path.Combine(source, "inside-trailing"), Unique("unused-copy")), "migration containment survives trailing separator");
            using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
            string canceledCopy = Unique("canceled-copy"), canceledBackup = Unique("canceled-backup");
            await Fails(() => LibraryMigrationService.CopyAndUpgradeAsync(source, canceledBackup, canceledCopy, cancelled.Token), "canceled migration creates no destination");
            Check(!Directory.Exists(canceledBackup) && !Directory.Exists(canceledCopy), "pre-cancel leaves source and destinations unchanged");
        }
    }

    private static async Task QuestionAssetMigration()
    {
        string path = CreateLegacy(2);
        await using (var upgraded = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite)))
            Check((await upgraded.InspectAsync()).SchemaVersion == 6, "question asset migration fixture initialized");
        using (var database = Db(path)) Sql(database, "DROP TABLE question_assets; PRAGMA user_version=4;");
        await using (var readOnly = await LibraryRepository.OpenAsync(new(path)))
            Check((await readOnly.InspectAsync()).SchemaVersion == 4, "v4 library remains readable without modification");
        await using var repository = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite));
        Check((await repository.InspectAsync()).SchemaVersion == 6, "v4 library upgrades to question image assets without repeating question migration");
        var document = await repository.GetDocumentAsync(Fixture<Document>("document").Id);
        Check((await repository.ReadAssetAsync(document.Pages[0].AssetId)).Bytes.SequenceEqual(png), "question asset migration preserves original source bytes");
        await repository.VerifyIntegrityAsync();
    }

    private static async Task ComplexCopyUpgradeRecovery()
    {
        string source = CreateLegacy(2), backup = Unique("complex-backup"), destination = Unique("complex-copy");
        var document = Fixture<Document>("document");
        var original = Fixture<Question>("question");
        var item = original.Items[0];
        var teacherGrade = item.Attempt!.Grade with { Source = GradeSource.Teacher, Reason = "合成教师批注：保留步骤分" };
        var teacherAttempt = item.Attempt with { Grade = teacherGrade, Teacher = new() { Raw = "√ +1", Regions = original.Sources, Grade = teacherGrade } };
        var deep = LegacyQuestion(original, "legacy-deep", "1(1)a", teacherAttempt, []);
        var complex = LegacyQuestion(original, "legacy-root", "1", null, [LegacyQuestion(original, "legacy-nested", "1(1)", teacherAttempt, [deep])]);
        static string Extend(string json, string value) => json[..^1] + ",\"legacy_unknown\":" + value + "}";
        using (var database = Db(source))
        {
            Sql(database, "ALTER TABLE assets ADD COLUMN legacy_blob BLOB");
            Sql(database, "UPDATE assets SET legacy_blob=X'0001FF',thumbnail_file='legacy-thumbnail.png'");
            Sql(database, "UPDATE source_pages SET page_index=page_index+10");
            Sql(database, "UPDATE source_pages SET page_index=11-page_index,rotation=90");
            Sql(database, "UPDATE providers SET config_json=$p0", Extend(DomainJson.Serialize(Fixture<Provider>("provider")), "{\"opaque\":\"\\u4e2d文\",\"flags\":[1,null,true]}"));
            Sql(database, "UPDATE settings SET settings_json=$p0", Extend(DomainJson.Serialize(Fixture<AppSettings>("settings")), "{\"legacy_order\":[3,1,2]}"));
            for (int history = 1; history <= 2; history++)
            {
                var job = Fixture<Job>("job") with { Id = history == 1 ? "71000000-0000-4000-8000-000000000001" : "71000000-0000-4000-8000-000000000002", CreatedAt = 1792281600000, State = JobState.Completed, Sequence = 2 };
                Sql(database, "INSERT INTO jobs(id,kind,state,created_at,updated_at,progress,message,snapshot_json) VALUES($p0,'grading','COMPLETED',$p1,$p2,1,$p3,$p4)",
                    job.Id, job.CreatedAt.ToString(), job.UpdatedAt.ToString(), job.Message, Extend(DomainJson.Serialize(job), "{\"history\":" + history + "}"));
                for (ulong sequence = 1; sequence <= 2; sequence++)
                {
                    var jobEvent = Event(job with { Sequence = sequence });
                    Sql(database, "INSERT INTO job_events(id,job_id,sequence,created_at,kind,payload_json) VALUES($p0,$p1,$p2,$p3,'snapshot',$p4)",
                        jobEvent.Id, job.Id, (long)sequence, jobEvent.CreatedAt.ToString(), Extend(DomainJson.Serialize(jobEvent), "{\"old_event\":true}"));
                }
                var references = JsonNode.Parse(DomainJson.Serialize(Fixture<List<ReferenceAnswer>>("references").Select(reference => reference with { Answer = "历史答案 " + history, MatchedItemKey = "legacy-root" }).ToList()))!;
                foreach (var reference in references.AsArray().OfType<JsonObject>()) { reference["matched_draft_key"] = reference["matched_item_key"]!.DeepClone(); reference.Remove("matched_item_key"); }
                var coverage = Fixture<List<Coverage>>("coverage").Select(item => item with { Reason = "历史覆盖 " + history }).ToList();
                Sql(database, "INSERT INTO grading_runs(job_id,document_id,input_json,coverage_json,references_json) VALUES($p0,$p1,$p2,$p3,$p4)",
                    job.Id, document.Id, Extend(DomainJson.Serialize(job.Input!), "{\"input_history\":" + history + "}"), DomainJson.Serialize(coverage), references.ToJsonString());
                Sql(database, "INSERT INTO model_calls(job_id,ordinal,details_json) VALUES($p0,0,$p1)", job.Id,
                    Extend(contracts.RootElement.GetProperty("model_call").GetRawText(), "{\"response_history\":" + history + ",\"raw_provider_field\":null}"));
            }
            Sql(database, "INSERT INTO questions(id,document_id,draft_key,content_json,updated_at) VALUES('72000000-0000-4000-8000-000000000001',$p0,'legacy-root',$p1,'2026-10-18T00:00:00Z')", document.Id, complex.ToJsonString());
            // A supported-version database can retain an extension table with types the UI never interprets.
            Sql(database, "CREATE TABLE legacy_annotations(id TEXT PRIMARY KEY,raw_json TEXT,sample BLOB,real_value REAL,null_value TEXT)");
            Sql(database, "INSERT INTO legacy_annotations VALUES('one',$p0,X'FF0010',1.25,NULL)", " { \"opaque\" : [ true, null, \"中文\" ] } ");
        }
        File.WriteAllBytes(Path.Combine(source, "assets", "legacy-thumbnail.png"), png);
        var databaseBefore = File.ReadAllBytes(Path.Combine(source, "study.sqlite3"));
        var rawBefore = RawRows(source);
        var assetsBefore = Directory.GetFiles(Path.Combine(source, "assets")).ToDictionary(path => Path.GetFileName(path)!, File.ReadAllBytes, StringComparer.Ordinal);
        var migration = await LibraryMigrationService.CopyAndUpgradeAsync(source + Path.DirectorySeparatorChar, backup, destination);
        Check(migration.OriginalSchemaVersion == 2 && migration.CurrentSchemaVersion == 6 && migration.Questions == 1, "complex v2 migration includes nested question and historical runs");
        Check(databaseBefore.SequenceEqual(File.ReadAllBytes(Path.Combine(source, "study.sqlite3"))), "complex migration leaves original database bytes unchanged");
        foreach (string path in new[] { backup, destination })
        {
            var rows = RawRows(path);
            var expectedRows = new Dictionary<string, List<string>>(rawBefore, StringComparer.Ordinal);
            foreach (string table in new[] { "paper_info", "question_assets" })
                if (rows.TryGetValue(table, out var added) && added.Count == 0 && !rawBefore.ContainsKey(table)) rows.Remove(table);
            if (path == destination) foreach (string restructured in new[] { "questions", "grading_runs" }) { rows.Remove(restructured); expectedRows.Remove(restructured); }
            Check(rows.Count == expectedRows.Count && rows.All(pair => expectedRows.TryGetValue(pair.Key, out var expected) && expected.SequenceEqual(pair.Value)),
                "complex raw rows preserve all columns, JSON bytes, historical runs, jobs/events and extension data: " + Path.GetFileName(path));
            Check(assetsBefore.All(pair => pair.Value.SequenceEqual(File.ReadAllBytes(Path.Combine(path, "assets", pair.Key!))))
                && Directory.GetFiles(Path.Combine(path, "assets")).Length == assetsBefore.Count, "complex original and thumbnail bytes preserved: " + Path.GetFileName(path));
        }
        await using (var copy = await LibraryRepository.OpenAsync(new(destination)))
        {
            var saved = (await copy.ListQuestionsAsync()).Single();
            var upgraded = saved.Question.Items.Single();
            Check(saved.Question.DraftKey == "legacy-root" && DomainJson.Serialize(saved.Question.Stem) == DomainJson.Serialize(original.Stem)
                && upgraded.Key == "legacy-deep" && upgraded.Label == "1(1)a" && upgraded.Prompt.Count == original.Stem.Count * 2
                && upgraded.Attempt!.Teacher!.Raw == "√ +1" && upgraded.Answer == item.Answer && saved.Question.Points == item.Points,
                "complex nested part tree flattens into an item that keeps branch text and teacher evidence");
            var result = await copy.GradingResultAsync(document.Id);
            Check(result.References.All(reference => reference.MatchedItemKey == "legacy-root"), "legacy reference matches are renamed without data loss");
            var summaries = await copy.QueryQuestionsAsync(new());
            Check(summaries.Total == 1 && summaries.Items.Single().Mistake && summaries.Items.Single().Verdict == Verdict.Partial && summaries.Items.Single().ItemCount == 1,
                "upgrade derives question bank summaries");
            Check((await copy.LoadJobSnapshotsAsync()).Count == 2 && (await copy.ListJobEventsAsync("71000000-0000-4000-8000-000000000001")).Count == 2,
                "complex legacy jobs and events remain readable after copy");
        }
        string trailingBackup = Path.Combine(source, "forbidden-backup");
        string trailingCopy = Path.Combine(source, "forbidden-copy");
        await Fails(() => LibraryMigrationService.CopyAndUpgradeAsync(source + Path.DirectorySeparatorChar, trailingBackup, Unique("outside-copy")), "trailing source separator still rejects contained backup");
        await Fails(() => LibraryMigrationService.CopyAndUpgradeAsync(source + Path.DirectorySeparatorChar, Unique("outside-backup"), trailingCopy), "trailing source separator still rejects contained migration copy");
        Check(!Directory.Exists(trailingBackup) && !Directory.Exists(trailingCopy), "contained paths rejected before creating backup or copy");
    }

    private static async Task QuestionBankScale()
    {
        const int count = 12_000;
        string path = CreateLegacy(2);
        var original = Fixture<Question>("question");
        var item = original.Items[0];
        Attempt Graded(Verdict verdict, double? earned) => item.Attempt! with { ReferenceKey = null, Grade = item.Attempt.Grade with { Verdict = verdict, Earned = earned, Source = GradeSource.AiSolution } };
        using (var database = Db(path))
        {
            Sql(database, "BEGIN");
            for (int index = 0; index < count; index++)
            {
                string key = "scale-" + index;
                var question = index % 3 == 0
                    ? LegacyQuestion(original, key, (index + 1).ToString(), null, [LegacyQuestion(original, key + "-1", "(1)", Graded(Verdict.Correct, 2), []), LegacyQuestion(original, key + "-2", "(2)", Graded(Verdict.Incorrect, 0), [])])
                    : LegacyQuestion(original, key, (index + 1).ToString(), index % 3 == 1 ? Graded(Verdict.Correct, 2) : Graded(Verdict.Uncertain, null), []);
                if (index % 500 == 0) question["content"] = new JsonArray(new JsonObject { ["kind"] = "text", ["text"] = "罕见关键词 光抑制 " + index });
                Sql(database, "INSERT INTO questions(id,document_id,draft_key,content_json,updated_at) VALUES($p0,$p1,$p2,$p3,'2026-10-18T00:00:00Z')",
                    Guid.NewGuid().ToString(), Fixture<Document>("document").Id, key, question.ToJsonString());
            }
            Sql(database, "COMMIT");
        }
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await using var repository = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite));
        long upgrade = clock.ElapsedMilliseconds;
        async Task<(QuestionPage Page, long Ms)> Timed(QuestionQuery query)
        {
            var timer = System.Diagnostics.Stopwatch.StartNew();
            var page = await repository.QueryQuestionsAsync(query);
            return (page, timer.ElapsedMilliseconds);
        }
        var first = await Timed(new());
        var deep = await Timed(new(Offset: count - 100));
        var search = await Timed(new(Search: "光抑制"));
        var mistakes = await Timed(new(QuestionFilter.Mistakes, QuestionSort.LowestScore));
        var subjectTimer = System.Diagnostics.Stopwatch.StartNew();
        var subjects = await repository.ListQuestionSubjectsAsync();
        long subjectMs = subjectTimer.ElapsedMilliseconds;
        Console.WriteLine($"Question bank scale: questions={count}; upgrade-ms={upgrade}; first-page-ms={first.Ms}; deep-page-ms={deep.Ms}; search-ms={search.Ms}; mistakes-ms={mistakes.Ms}; subjects-ms={subjectMs}");
        Check(first.Page.Total == count && first.Page.Items.Count == 100 && deep.Page.Items.Count == 100 && deep.Page.Items[^1].Number == count.ToString(), "twelve thousand upgraded questions page in stable order");
        Check(search.Page.Matching == count / 500 && mistakes.Page.Matching == count / 3 && mistakes.Page.Items.All(summary => summary.Mistake && summary.ItemCount == 2) && subjects.SequenceEqual([original.Subject]), "large bank search, mistake filter and subjects stay correct");
        Check(new[] { first.Ms, deep.Ms, search.Ms, mistakes.Ms, subjectMs }.All(ms => ms < 2000), "large bank queries stay interactive");
    }

    private static JsonObject LegacyQuestion(Question template, string key, string number, Attempt? attempt, JsonObject[] parts)
    {
        var item = template.Items[0];
        return new JsonObject
        {
            ["draft_key"] = key,
            ["number"] = number,
            ["subject"] = template.Subject,
            ["question_type"] = template.QuestionType,
            ["content"] = JsonNode.Parse(DomainJson.Serialize(template.Stem)),
            ["options"] = new JsonArray(item.Options.Select(option => (JsonNode?)JsonValue.Create(option)).ToArray()),
            ["points"] = item.Points,
            ["standard_answer"] = item.Answer,
            ["explanation"] = item.Explanation,
            ["knowledge_points"] = new JsonArray(template.KnowledgePoints.Select(point => (JsonNode?)JsonValue.Create(point)).ToArray()),
            ["sources"] = JsonNode.Parse(DomainJson.Serialize(template.Sources)),
            ["uncertainties"] = new JsonArray(),
            ["attempt"] = attempt is null ? null : JsonNode.Parse(DomainJson.Serialize(attempt)),
            ["parts"] = new JsonArray(parts.Select(part => (JsonNode?)part).ToArray())
        };
    }

    private static Dictionary<string, List<string>> RawRows(string path)
    {
        using var database = Db(path);
        var tables = new List<string>();
        using (var command = database.CreateCommand())
        {
            command.CommandText = "SELECT name FROM sqlite_master WHERE type='table' AND name NOT LIKE 'sqlite_%' ORDER BY name";
            using var reader = command.ExecuteReader(); while (reader.Read()) tables.Add(reader.GetString(0));
        }
        var result = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (string table in tables)
        {
            using var command = database.CreateCommand();
            command.CommandText = "SELECT * FROM \"" + table.Replace("\"", "\"\"", StringComparison.Ordinal) + "\" ORDER BY rowid";
            using var reader = command.ExecuteReader(); var rows = new List<string>();
            while (reader.Read())
            {
                var values = new List<string>();
                for (int index = 0; index < reader.FieldCount; index++)
                {
                    object value = reader.GetValue(index);
                    string exact = value switch
                    {
                        DBNull => "null",
                        byte[] bytes => "blob:" + Convert.ToBase64String(bytes),
                        string text => "text:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)),
                        long integer => "integer:" + integer.ToString(System.Globalization.CultureInfo.InvariantCulture),
                        double number => "real:" + BitConverter.DoubleToInt64Bits(number).ToString(System.Globalization.CultureInfo.InvariantCulture),
                        _ => throw new InvalidDataException("Unexpected SQLite storage class")
                    };
                    values.Add(reader.GetName(index) + "=" + exact);
                }
                rows.Add(string.Join("|", values));
            }
            result.Add(table, rows);
        }
        return result;
    }

    private static async Task DirectoryPromotionLocks()
    {
        var stage = Unique("locked-stage"); Directory.CreateDirectory(stage);
        await File.WriteAllTextAsync(Path.Combine(stage, "original.txt"), "retained bytes");
        var destination = Unique("promoted-stage");
        using var held = new FileStream(Path.Combine(stage, "original.txt"), FileMode.Open, FileAccess.Read, FileShare.Read);
        Check(held.CanRead, "test holds Windows file handle without delete sharing");
        bool initiallyLocked = false;
        try { Directory.Move(stage, destination); } catch (IOException) { initiallyLocked = true; }
        Check(initiallyLocked && Directory.Exists(stage), "Windows blocks promotion while directory is held");
        var release = Task.Run(async () => { await Task.Delay(150); held.Dispose(); });
        LibraryRepository.PromoteBackupDirectory(stage, destination, CancellationToken.None);
        await release;
        Check(await File.ReadAllTextAsync(Path.Combine(destination, "original.txt")) == "retained bytes", "promotion recovers after temporary directory lock releases");
        var cancelledStage = Unique("cancelled-stage"); Directory.CreateDirectory(cancelledStage);
        string lockedFile = Path.Combine(cancelledStage, "held.txt"); await File.WriteAllTextAsync(lockedFile, "retained bytes");
        using var locked = new FileStream(lockedFile, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var cancellation = new CancellationTokenSource(80);
        bool cancelled = false;
        try { LibraryRepository.PromoteBackupDirectory(cancelledStage, Unique("cancelled-destination"), cancellation.Token); }
        catch (OperationCanceledException) { cancelled = true; }
        Check(cancelled && Directory.Exists(cancelledStage), "cancelled promotion preserves staged data");
    }

    private static async Task ManagedLibraryLocation()
    {
        var path = Unique("managed-library"); var copy = Unique("relocated-library");
        string source = Path.Combine(Root, "source-to-delete.png");
        await File.WriteAllBytesAsync(source, png);
        string documentId, assetId;
        var provider = Fixture<Provider>("provider");
        var model = provider.Models[0] with { DisplayName = "复习助手", ContextWindow = 200000 };
        provider = provider with { Models = [model] };
        await using (var library = await LibraryRepository.OpenAsync(NewOptions(path)))
        {
            var document = await library.ImportDocumentAsync("保留原件", ImportMode.Blank, [new(source, await File.ReadAllBytesAsync(source))]);
            documentId = document.Id; assetId = document.Pages[0].AssetId;
            File.Delete(source);
            Check((await library.ReadAssetAsync(assetId)).Bytes.SequenceEqual(png), "managed original survives deletion of source file");
            await library.UpsertProviderAsync(provider);
            await library.SaveSettingsAsync(AppSettings.Default with { LegacyNoticeDismissed = true });
            await library.BackupAsync(copy);
            await Fails(() => library.BackupAsync(copy), "relocation never overwrites an existing destination");
            await Fails(() => library.BackupAsync(Path.Combine(path, "nested")), "relocation rejects copying inside active library");
        }
        await using (var relocated = await LibraryRepository.OpenAsync(new(copy, LibraryOpenMode.ReadWrite)))
        {
            Check((await relocated.GetDocumentAsync(documentId)).Pages[0].AssetId == assetId
                && (await relocated.ReadAssetAsync(assetId)).Bytes.SequenceEqual(png), "relocated library retains document and original bytes");
            var saved = (await relocated.ListProvidersAsync()).Single();
            Check(saved.Models.Single().DisplayName == "复习助手" && saved.Models.Single().ContextWindow == 200000
                && saved.KeyRef == provider.KeyRef, "model metadata and credential references survive relocation");
            Check((await relocated.GetSettingsAsync()).LegacyNoticeDismissed, "legacy notice dismissal survives copy and reopen");
        }
        await using var original = await LibraryRepository.OpenAsync(new(path, LibraryOpenMode.ReadWrite));
        Check((await original.ReadAssetAsync(assetId)).Bytes.SequenceEqual(png), "original library remains available after relocation");
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
        var unchanged = await repository.EditQuestionAsync(saved.Id, question with { Stem = [.. question.Stem, new TextContent { Text = "补充" }] });
        Check(unchanged.Question.Items[0].Attempt!.Grade.Source == GradeSource.AiReference, "editing question text keeps unchanged AI grades");
        var noted = await repository.SaveQuestionNotesAsync(saved.Id, "复习：注意定义域");
        Check(noted.Question.Notes == "复习：注意定义域" && noted.Question.Items[0].Attempt!.Grade.Source == GradeSource.AiReference, "notes save without changing grades");
        var correctedAttempt = question.Items[0].Attempt! with { StudentAnswer = "更正后的作答" };
        var edited = await repository.EditQuestionAsync(saved.Id, noted.Question with { Items = [question.Items[0] with { Attempt = correctedAttempt }] });
        Check(edited.Question.Items[0].Attempt!.Grade.Source == GradeSource.Manual && edited.Question.Notes == "复习：注意定义域", "editing a grade marks manual provenance");
        var page = await repository.QueryQuestionsAsync(new(QuestionFilter.Mistakes, Search: "注意定义域"));
        Check(page.Matching == 1 && page.Items.Single().Id == saved.Id && page.Items.Single().Possible == 2, "question bank searches notes and filters mistakes");
        Check((await repository.ListQuestionSubjectsAsync()).SequenceEqual([question.Subject]) && (await repository.QueryQuestionsAsync(new(Subject: question.Subject))).Matching == 1, "question bank lists and filters subjects");
        Check((await repository.QueryQuestionsAsync(new(QuestionFilter.Correct))).Matching == 0 && (await repository.QueryQuestionsAsync(new(Search: "100%_"))).Matching == 0, "question bank filters escape wildcards");
        var rerun = job with { Id = Guid.NewGuid().ToString("D"), Sequence = 1, CreatedAt = job.CreatedAt + 1 };
        await repository.SaveJobEventAsync(rerun, Event(rerun)); await repository.BeginGradingAsync(rerun);
        var updated = await repository.SaveGradingQuestionAsync(rerun.Id, question);
        Check(updated.Id == saved.Id && updated.Question.Items[0].Attempt!.Grade.Source == GradeSource.Manual && updated.Question.Notes == "复习：注意定义域", "regrade preserves manual grade and notes");
        await Fails(() => repository.SaveGradingQuestionAsync(rerun.Id, question with { Items = [question.Items[0] with { Attempt = null }] }), "regrade cannot remove protected attempt");
        await Fails(() => repository.SaveGradingQuestionAsync(rerun.Id, question with { Items = [question.Items[0] with { Key = "replacement" }] }), "regrade cannot drop protected item");
        await Fails(() => repository.UpdatePagesAsync(job.Input.Document.Id, []), "active run blocks page edit");
        await Fails(() => repository.DeletePaperAsync(job.Input.Document.Id), "active run blocks paper deletion");
        var completed = rerun with { State = JobState.Completed, Sequence = 2, Progress = 100 };
        await repository.SaveJobEventAsync(completed, Event(completed));
        string original = Path.Combine(path, "assets", "10000000-0000-4000-8000-000000000001.png");
        Check(File.Exists(original), "paper asset exists before deletion");
        await repository.DeletePaperAsync(job.Input.Document.Id);
        Check((await repository.ListPapersAsync()).Count == 0 && (await repository.GradingQuestionsAsync(job.Input.Document.Id)).Count == 0,
            "deleting a paper removes its row and saved questions");
        Check((await repository.LoadJobSnapshotsAsync()).Count == 0, "deleting a paper removes its task history");
        Check(!File.Exists(original), "deleting a paper removes its managed original and thumbnail files");
        using (var db = Db(path))
            Check(Convert.ToInt32(Value(db, "SELECT COUNT(*) FROM grading_runs")) == 0
                && Convert.ToInt32(Value(db, "SELECT COUNT(*) FROM questions")) == 0
                && Convert.ToInt32(Value(db, "SELECT COUNT(*) FROM source_pages")) == 0
                && Convert.ToInt32(Value(db, "SELECT COUNT(*) FROM assets")) == 0,
                "deleting a paper clears related runs, questions, pages and assets");
        await Fails(() => repository.DeletePaperAsync(job.Input.Document.Id), "deleting a missing paper is rejected");
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
