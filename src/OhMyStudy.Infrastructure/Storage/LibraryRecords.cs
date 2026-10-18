using System.Text;
using System.Text.Json;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Storage;

public sealed partial class LibraryRepository
{
    public Task<List<Document>> ListDocumentsAsync(CancellationToken cancellationToken = default) => Run(() =>
        Query("SELECT id FROM documents ORDER BY created_at DESC,id", r => r.GetString(0)).Select(Document).ToList(), cancellationToken);

    public Task<Document> GetDocumentAsync(string id, CancellationToken cancellationToken = default)
    { ValidId(id); return Run(() => Document(id), cancellationToken); }

    private Document Document(string id)
    {
        var document = Query("SELECT id,name,mode,created_at,pending_pdf_pages FROM documents WHERE id=$p0", r => new Document
        {
            Id = r.GetString(0),
            Name = r.GetString(1),
            Mode = DomainJson.Deserialize<ImportMode>(r.GetString(2)),
            CreatedAt = r.GetString(3),
            PendingPdfPages = r.GetBoolean(4),
            Pages = []
        }, id).SingleOrDefault() ?? throw new KeyNotFoundException("未找到材料");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version"));
        var suffix = version >= 2 ? "p.original_asset_id,p.pdf_page_index" : "NULL,NULL";
        return document with
        {
            Pages = Query("SELECT p.id,p.asset_id,p.page_index,p.rotation,a.width,a.height,p.preview_status,p.role," + suffix + " FROM source_pages p JOIN assets a ON a.id=p.asset_id WHERE p.document_id=$p0 ORDER BY p.page_index", r => new SourcePage
            {
                Id = r.GetString(0),
                AssetId = r.GetString(1),
                PageIndex = checked((uint)r.GetInt64(2)),
                Rotation = checked((ushort)r.GetInt32(3)),
                Width = NullableUInt(r, 4),
                Height = NullableUInt(r, 5),
                PreviewStatus = r.GetString(6),
                Role = r.GetString(7),
                OriginalAssetId = NullableText(r, 8),
                PdfPageIndex = NullableUInt(r, 9)
            }, id)
        };
    }

    public Task<Document> UpdatePagesAsync(string documentId, IReadOnlyList<PageEdit> edits, CancellationToken cancellationToken = default)
    {
        ValidId(documentId);
        var pages = edits.ToArray();
        var unique = new HashSet<string>(StringComparer.Ordinal);
        foreach (var page in pages)
        {
            ValidId(page.Id);
            if (page.Rotation is not (0 or 90 or 180 or 270) || !unique.Add(page.Id)) throw new ArgumentException("页序或方向无效");
        }
        return Run(() => Transaction(cancellationToken, () =>
        {
            DocumentIdle(documentId);
            var document = Document(documentId);
            if (pages.Any(p => !document.Pages.Any(old => old.Id == p.Id))) throw new ArgumentException("页面不属于此材料");
            foreach (var old in document.Pages)
                if (!unique.Contains(old.Id) && PageHasResults(documentId, old.Id))
                    throw new InvalidOperationException("该页仍被题目或参考答案引用，请先移除引用再删除页面");
            var offset = Convert.ToInt64(Scalar("SELECT COALESCE(MAX(page_index),0)+1 FROM source_pages WHERE document_id=$p0", documentId)) + pages.Length;
            Execute("UPDATE source_pages SET page_index=page_index+$p0 WHERE document_id=$p1", offset, documentId);
            foreach (var old in document.Pages)
                if (!unique.Contains(old.Id)) Execute("DELETE FROM source_pages WHERE id=$p0 AND document_id=$p1", old.Id, documentId);
            for (var index = 0; index < pages.Length; index++)
                Execute("UPDATE source_pages SET page_index=$p0,rotation=$p1 WHERE id=$p2 AND document_id=$p3", index, pages[index].Rotation, pages[index].Id, documentId);
            Execute("UPDATE documents SET pending_pdf_pages=EXISTS(SELECT 1 FROM source_pages WHERE document_id=$p0 AND preview_status='pending_pdf') WHERE id=$p0", documentId);
            return Document(documentId);
        }), cancellationToken, true);
    }

    private void DocumentIdle(string documentId)
    {
        foreach (var json in Query("SELECT snapshot_json FROM jobs WHERE state IN ('QUEUED','RUNNING','WAITING_PERMISSION')", r => r.GetString(0)))
        {
            using var value = JsonDocument.Parse(json);
            if (value.RootElement.TryGetProperty("input", out var input) && input.ValueKind == JsonValueKind.Object &&
                input.TryGetProperty("document", out var document) && document.TryGetProperty("id", out var id) && id.GetString() == documentId)
                throw new InvalidOperationException("该材料正在处理，请先取消任务再编辑页面或结果");
        }
    }

    private bool PageHasResults(string documentId, string pageId) =>
        Questions(documentId).Any(q => ContainsProperty(DomainJson.Serialize(q.Question), "page_id", pageId)) ||
        References(documentId).Any(r => ContainsProperty(DomainJson.Serialize(r), "page_id", pageId));

    private static bool ContainsProperty(string json, string key, string text)
    {
        using var value = JsonDocument.Parse(json);
        return Contains(value.RootElement);
        bool Contains(JsonElement element) => element.ValueKind switch
        {
            JsonValueKind.Object => element.EnumerateObject().Any(p => p.Name == key && p.Value.ValueKind == JsonValueKind.String && p.Value.GetString() == text || Contains(p.Value)),
            JsonValueKind.Array => element.EnumerateArray().Any(Contains),
            _ => false
        };
    }

    public Task<List<Provider>> ListProvidersAsync(CancellationToken cancellationToken = default) => Run(() =>
        Query("SELECT config_json FROM providers ORDER BY id", r => DomainJson.Deserialize<Provider>(r.GetString(0))), cancellationToken);

    public Task<Provider> UpsertProviderAsync(Provider provider, CancellationToken cancellationToken = default)
    {
        ValidateProvider(provider);
        var stored = DomainJson.Deserialize<Provider>(DomainJson.Serialize(provider with { HasKey = false }));
        return Run(() => Transaction(cancellationToken, () =>
        {
            Execute("INSERT INTO providers(id,config_json) VALUES($p0,$p1) ON CONFLICT(id) DO UPDATE SET config_json=excluded.config_json", stored.Id, DomainJson.Serialize(stored));
            return stored;
        }), cancellationToken, true);
    }

    public Task DeleteProviderAsync(string id, CancellationToken cancellationToken = default)
    { ValidId(id); return Run(() => Transaction(cancellationToken, () => Execute("DELETE FROM providers WHERE id=$p0", id)), cancellationToken, true); }

    public Task<AppSettings> GetSettingsAsync(CancellationToken cancellationToken = default) => Run(() =>
        Scalar("SELECT settings_json FROM settings WHERE id=1") is string json ? DomainJson.Deserialize<AppSettings>(json) : AppSettings.Default, cancellationToken);

    public Task<AppSettings> SaveSettingsAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        ConfigurationValidation.ValidateSettings(settings);
        return Run(() => Transaction(cancellationToken, () =>
        {
            Execute("INSERT INTO settings(id,settings_json) VALUES(1,$p0) ON CONFLICT(id) DO UPDATE SET settings_json=excluded.settings_json", DomainJson.Serialize(settings));
            return settings;
        }), cancellationToken, true);
    }

    internal static void ValidateProvider(Provider provider)
    {
        ConfigurationValidation.ValidateProvider(provider);
    }
}
