using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Grading;

internal sealed class MinerUClient(LibraryRepository library, IGradingImageReader images) : IDisposable
{
    private readonly HttpClient http = new(new SocketsHttpHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(3) };

    public async Task<List<OcrPage>> RecognizeAsync(JobContext context, IReadOnlyList<SourcePage> pages, string token, CancellationToken cancellationToken)
    {
        await context.ActivityAsync(new JobActivity { Key = "upload", Kind = "upload", Title = "Upload pages to MinerU", Detail = $"Uploaded pages: 0/{pages.Count}." }).ConfigureAwait(false);
        var files = new JsonArray();
        var uploads = new List<byte[]>();
        foreach (var page in pages)
        {
            var asset = await library.ReadAssetAsync(page.AssetId, cancellationToken: cancellationToken).ConfigureAwait(false);
            var rotated = page.Rotation == 0 ? null : await images.ReadOcrAsync(asset.Bytes, page.Rotation, cancellationToken).ConfigureAwait(false);
            uploads.Add(rotated is null ? asset.Bytes : Convert.FromBase64String(rotated.Image.Base64));
            var extension = (rotated?.Image.Mime ?? asset.Mime) switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", _ => throw new InvalidDataException("MinerU requires a rendered source page.") };
            files.Add((JsonNode)new JsonObject { ["name"] = page.Id + extension, ["data_id"] = page.Id, ["is_ocr"] = true });
        }
        var body = new JsonObject { ["files"] = files, ["model_version"] = "vlm", ["enable_formula"] = true, ["enable_table"] = true, ["language"] = "ch" };
        var batch = await ApiAsync(HttpMethod.Post, "file-urls/batch", body, token, cancellationToken).ConfigureAwait(false);
        var urls = batch.GetProperty("file_urls").EnumerateArray().Select(v => v.GetString()!).ToArray();
        if (urls.Length != pages.Count) throw new InvalidDataException("MinerU returned an unexpected number of upload URLs.");
        var batchId = batch.GetProperty("batch_id").GetString()!;
        await SaveAsync(context, "mineru-batch.json", "application/json", Encoding.UTF8.GetBytes(new JsonObject { ["batch_id"] = batchId }.ToJsonString()), cancellationToken).ConfigureAwait(false);
        for (int index = 0; index < pages.Count; index++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Put, Https(urls[index])) { Content = new ByteArrayContent(uploads[index]) };
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw new HttpRequestException($"MinerU page upload failed: page={pages[index].Id}; status={(int)response.StatusCode}.");
            await context.ActivityAsync(new JobActivity { Key = "upload", Kind = "upload", Title = "Upload pages to MinerU",
                Detail = $"Uploaded pages: {index + 1}/{pages.Count}.", State = index + 1 == pages.Count ? "completed" : "running" }).ConfigureAwait(false);
        }
        await context.ActivityAsync(new JobActivity { Key = "ocr", Kind = "ocr", Title = "Recognize page with MinerU", Detail = $"OCR pages: 0/{pages.Count}." }).ConfigureAwait(false);
        var result = new Dictionary<string, OcrPage>(StringComparer.Ordinal);
        while (result.Count < pages.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var status = await ApiAsync(HttpMethod.Get, "extract-results/batch/" + Uri.EscapeDataString(batchId), null, token, cancellationToken).ConfigureAwait(false);
            foreach (var item in status.GetProperty("extract_result").EnumerateArray())
            {
                var id = item.GetProperty("data_id").GetString()!;
                var page = pages.Single(p => p.Id == id);
                if (result.ContainsKey(id)) continue;
                var state = item.GetProperty("state").GetString();
                await context.ActivityAsync(new JobActivity { Key = "ocr-" + id, Kind = "ocr", Title = "Recognize page with MinerU", PageId = id,
                    Detail = state is null or "pending" ? "Queued" : state, State = state == "done" ? "completed" : state == "failed" ? "failed" : "running", PagesRead = result.Count }).ConfigureAwait(false);
                if (state == "failed") throw new InvalidOperationException($"MinerU failed: page={id}; error={item.GetProperty("err_msg").GetString()}.");
                if (state != "done") continue;
                using var response = await http.GetAsync(Https(item.GetProperty("full_zip_url").GetString()!), cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
                result.Add(id, await ReadArchiveAsync(context, page, bytes, cancellationToken).ConfigureAwait(false));
                await context.ProgressAsync(5 + 20d * result.Count / pages.Count, $"OCR pages: {result.Count}/{pages.Count}.").ConfigureAwait(false);
            }
            if (result.Count < pages.Count) await Task.Delay(TimeSpan.FromSeconds(3), cancellationToken).ConfigureAwait(false);
        }
        var ordered = pages.Select(p => result[p.Id]).ToList();
        await context.ActivityAsync(new JobActivity { Key = "ocr", Kind = "ocr", Title = "Recognize page with MinerU", State = "completed",
            Detail = $"OCR pages: {pages.Count}/{pages.Count}.", PagesRead = pages.Count }).ConfigureAwait(false);
        var markdown = string.Join("\n\n", ordered.Select(p => $"<!-- page_id: {p.PageId} -->\n" + p.Markdown
            .Replace("](images/", $"]({p.PageId}/images/").Replace("](./images/", $"]({p.PageId}/images/")
            .Replace("src=\"images/", $"src=\"{p.PageId}/images/").Replace("src='images/", $"src='{p.PageId}/images/")));
        await SaveAsync(context, "full.md", "text/markdown", Encoding.UTF8.GetBytes(markdown), cancellationToken).ConfigureAwait(false);
        await SaveAsync(context, "ocr.json", "application/json", Encoding.UTF8.GetBytes(DomainJson.Serialize(ordered)), cancellationToken).ConfigureAwait(false);
        return ordered;
    }

    private async Task<OcrPage> ReadArchiveAsync(JobContext context, SourcePage page, byte[] bytes, CancellationToken cancellationToken)
    {
        await SaveAsync(context, page.Id + "/mineru.zip", "application/zip", bytes, cancellationToken).ConfigureAwait(false);
        using var archive = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
        var markdownEntry = archive.Entries.Single(e => e.Name == "full.md");
        var listEntry = archive.Entries.Single(e => e.Name.EndsWith("_content_list.json", StringComparison.Ordinal));
        string archiveRoot = markdownEntry.FullName[..^markdownEntry.Name.Length];
        foreach (var entry in archive.Entries.Where(e => e.Name.Length > 0 && e.FullName.StartsWith(archiveRoot + "images/", StringComparison.Ordinal)))
        {
            await using var imageStream = entry.Open();
            using var imageBuffer = new MemoryStream();
            await imageStream.CopyToAsync(imageBuffer, cancellationToken).ConfigureAwait(false);
            await SaveAsync(context, page.Id + "/" + entry.FullName[archiveRoot.Length..], "application/octet-stream", imageBuffer.ToArray(), cancellationToken).ConfigureAwait(false);
        }
        static async Task<string> TextAsync(ZipArchiveEntry entry, CancellationToken token)
        {
            using var reader = new StreamReader(entry.Open());
            return await reader.ReadToEndAsync(token).ConfigureAwait(false);
        }
        var markdown = await TextAsync(markdownEntry, cancellationToken).ConfigureAwait(false);
        await SaveAsync(context, page.Id + "/full.md", "text/markdown", Encoding.UTF8.GetBytes(markdown), cancellationToken).ConfigureAwait(false);
        var json = await TextAsync(listEntry, cancellationToken).ConfigureAwait(false);
        await SaveAsync(context, page.Id + "/content_list.json", "application/json", Encoding.UTF8.GetBytes(json), cancellationToken).ConfigureAwait(false);
        using var list = JsonDocument.Parse(json);
        var blocks = new List<OcrBlock>();
        int index = 0;
        foreach (var item in list.RootElement.EnumerateArray())
        {
            var kind = item.GetProperty("type").GetString()!;
            if (!item.TryGetProperty("bbox", out var box)) throw new InvalidDataException("MinerU content_list must include block bounding boxes in 0..1000 coordinates.");
            var b = box.EnumerateArray().Select(v => v.GetDouble() / 1000d).ToArray();
            if (b.Length != 4) throw new InvalidDataException("MinerU returned an invalid block rectangle.");
            var region = SourceRegion(page, b);
            GradingValidation.ValidateRegion(region, new Document { Id = "ocr", Name = "", Mode = ImportMode.Auto, CreatedAt = "", PendingPdfPages = false, Pages = [page] }, page.Role);
            string value = item.TryGetProperty("text", out var text) ? text.GetString() ?? "" : item.TryGetProperty("table_body", out var table) ? table.GetString() ?? "" : "";
            if (item.TryGetProperty("list_items", out var listItems)) value = string.Join("\n", listItems.EnumerateArray().Select(v => v.GetString()));
            if (item.TryGetProperty("code_body", out var code)) value = code.GetString() ?? "";
            string? assetId = null;
            if (item.TryGetProperty("img_path", out var path) && !string.IsNullOrWhiteSpace(path.GetString()))
            {
                string relative = path.GetString()!.Replace('\\', '/');
                var entry = archive.Entries.Single(e => e.FullName == relative || e.FullName.EndsWith("/" + relative, StringComparison.Ordinal));
                using var stream = entry.Open();
                using var output = new MemoryStream();
                await stream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
                var image = output.ToArray();
                var asset = await context.CommitAsync(ct => library.SaveGradingImageAsync(context.JobId, image, ct)).ConfigureAwait(false);
                assetId = asset.Id;
                if (value.Length == 0) value = $"![]({page.Id}/{relative})";
            }
            foreach (var captionName in new[] { "image_caption", "image_footnote", "table_caption", "table_footnote" })
                if (item.TryGetProperty(captionName, out var captions))
                    value += "\n" + string.Join("\n", captions.EnumerateArray().Select(v => v.GetString()));
            blocks.Add(new(page.Id + "-b" + index++, kind, value.Trim(), region, assetId));
        }
        return new(page.Id, markdown, blocks);
    }

    private async Task<JsonElement> ApiAsync(HttpMethod method, string path, JsonObject? body, string token, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, "https://mineru.net/api/v4/" + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
        string text = "";
        int? status = null;
        try
        {
            using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
            status = (int)response.StatusCode;
            text = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var json = JsonDocument.Parse(text);
            var root = json.RootElement;
            if (!response.IsSuccessStatusCode || root.GetProperty("code").GetInt32() != 0)
                throw new InvalidOperationException($"MinerU rejected {path}: HTTP {(int)response.StatusCode}; {DiagnosticRedaction.Text(text, [token])}");
            return root.GetProperty("data").Clone();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            OperationDiagnostics.Record("MinerU.Failed", new() { ["path"] = path, ["status"] = status, ["request"] = body?.DeepClone(),
                ["response"] = DiagnosticRedaction.Text(text, [token]), ["exception"] = DiagnosticRedaction.Text(error.ToString(), [token]) }, true);
            throw;
        }
    }

    private Task SaveAsync(JobContext context, string name, string mime, byte[] bytes, CancellationToken cancellationToken)
        => context.CommitAsync(ct => library.SaveWorkflowArtifactAsync(context.JobId, name, mime, bytes, ct));
    private static Uri Https(string url) => Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https" && uri.UserInfo.Length == 0
        ? uri : throw new InvalidDataException("MinerU returned an invalid HTTPS storage URL.");
    private static Region SourceRegion(SourcePage page, double[] box)
    {
        double x = box[0], y = box[1], width = box[2] - box[0], height = box[3] - box[1];
        return page.Rotation switch
        {
            0 => new() { PageId = page.Id, X = x, Y = y, Width = width, Height = height },
            90 => new() { PageId = page.Id, X = y, Y = 1 - box[2], Width = height, Height = width },
            180 => new() { PageId = page.Id, X = 1 - box[2], Y = 1 - box[3], Width = width, Height = height },
            270 => new() { PageId = page.Id, X = 1 - box[3], Y = x, Width = height, Height = width },
            _ => throw new InvalidDataException("Unsupported OCR page rotation.")
        };
    }
    public void Dispose() => http.Dispose();
}
