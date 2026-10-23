using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Providers;

internal sealed class ProviderFileSession(HttpClient http, Protocol protocol, Uri endpoint, IReadOnlyDictionary<string, string> headers)
{
    private readonly Dictionary<string, string> _uploaded = new(StringComparer.Ordinal);
    private const int ExpirationSeconds = 3600;

    public async Task<JsonArray> PrepareAsync(ProviderCredentials credentials, JsonArray history, Action<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        var prepared = (JsonArray)history.DeepClone();
        foreach (var item in prepared.OfType<JsonObject>())
        {
            var parts = item[protocol == Protocol.Responses && Text(item, "type") == "function_call_output" ? "output" : "content"] as JsonArray;
            if (parts is not null) await ReplaceAsync(credentials, parts, progress, cancellationToken).ConfigureAwait(false);
        }
        return prepared;
    }

    private async Task ReplaceAsync(ProviderCredentials credentials, JsonArray parts, Action<ProviderProgress>? progress, CancellationToken cancellationToken)
    {
        foreach (var part in parts.OfType<JsonObject>())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (protocol == Protocol.Messages && Text(part, "type") == "tool_result" && part["content"] is JsonArray nested)
            {
                await ReplaceAsync(credentials, nested, progress, cancellationToken).ConfigureAwait(false);
                continue;
            }
            string? mime = null, base64 = null;
            if (protocol == Protocol.Responses && Text(part, "type") == "input_image" && Text(part, "image_url") is { } url && url.StartsWith("data:", StringComparison.Ordinal))
            {
                int separator = url.IndexOf(";base64,", StringComparison.Ordinal);
                if (separator < 5) throw ProviderProtocol.Invalid("invalid inline image");
                mime = url[5..separator]; base64 = url[(separator + 8)..];
            }
            else if (protocol == Protocol.Messages && Text(part, "type") == "image" && part["source"] is JsonObject source && Text(source, "type") == "base64")
            {
                mime = Text(source, "media_type"); base64 = Text(source, "data");
            }
            if (base64 is null) continue;
            string extension = mime switch { "image/png" => ".png", "image/jpeg" => ".jpg", "image/webp" => ".webp", "image/gif" => ".gif", _ => throw ProviderProtocol.Invalid("unsupported file image type") };
            byte[] bytes;
            try { bytes = Convert.FromBase64String(base64); }
            catch (FormatException) { throw ProviderProtocol.Invalid("invalid image encoding"); }
            string hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            string key = mime + ":" + hash;
            if (!_uploaded.TryGetValue(key, out string? fileId))
            {
                progress?.Invoke(new(Status: "Uploading images to the provider Files API"));
                fileId = await UploadAsync(credentials, bytes, mime!, hash + extension, cancellationToken).ConfigureAwait(false);
                _uploaded.Add(key, fileId);
            }
            if (protocol == Protocol.Responses)
            {
                part.Remove("image_url"); part["file_id"] = fileId;
            }
            else part["source"] = new JsonObject { ["type"] = "file", ["file_id"] = fileId };
        }
    }

    private async Task<string> UploadAsync(ProviderCredentials credentials, byte[] bytes, string mime, string filename, CancellationToken cancellationToken)
    {
        var fields = new JsonObject { ["filename"] = filename, ["mime"] = mime, ["bytes"] = bytes.Length, ["expires_in_seconds"] = ExpirationSeconds };
        using var request = Request(HttpMethod.Post, "files", credentials);
        var form = new MultipartFormDataContent(); request.Content = form;
        if (protocol == Protocol.Responses)
        {
            fields["purpose"] = "user_data";
            form.Add(new StringContent("user_data"), "purpose");
            form.Add(new StringContent("created_at"), "expires_after[anchor]");
            form.Add(new StringContent("3600"), "expires_after[seconds]");
        }
        else form.Add(new StringContent("3600"), "expires_in_seconds");
        var content = new ByteArrayContent(bytes); content.Headers.ContentType = new MediaTypeHeaderValue(mime);
        form.Add(content, "file", filename);
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            var body = await ReadAsync(response, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode) throw Rejected(response, body);
            string? id = ProviderProtocol.Str(body, "id");
            if (string.IsNullOrWhiteSpace(id) || id.Length > 512) throw ProviderProtocol.ProtocolError("the Files API returned an invalid file ID");
            fields["file_id"] = id;
            OperationDiagnostics.Record("Files.Uploaded", fields);
            return id;
        }
        catch (Exception error)
        {
            fields["exception"] = error.ToString();
            OperationDiagnostics.Record("Files.UploadFailed", fields, true);
            throw;
        }
    }

    public async Task DeleteAsync(ProviderCredentials credentials, CancellationToken cancellationToken)
    {
        var failures = new List<Exception>();
        foreach (string id in _uploaded.Values.Distinct(StringComparer.Ordinal).ToArray())
        {
            try
            {
                using var request = Request(HttpMethod.Delete, "files/" + Uri.EscapeDataString(id), credentials);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode && response.StatusCode != HttpStatusCode.NotFound)
                    throw Rejected(response, await ReadAsync(response, cancellationToken).ConfigureAwait(false));
                foreach (string key in _uploaded.Where(p => p.Value == id).Select(p => p.Key).ToArray()) _uploaded.Remove(key);
                OperationDiagnostics.Record("Files.Deleted", new() { ["file_id"] = id });
            }
            catch (Exception error)
            {
                failures.Add(error);
                OperationDiagnostics.Record("Files.DeleteFailed", new() { ["file_id"] = id, ["exception"] = error.ToString(), ["requested_expiration_seconds"] = ExpirationSeconds }, true);
            }
        }
        if (failures.Count > 0) throw new AggregateException("Some uploaded files could not be deleted. Their requested expiration is one hour after upload.", failures);
    }

    private HttpRequestMessage Request(HttpMethod method, string path, ProviderCredentials credentials)
    {
        var request = new HttpRequestMessage(method, new Uri(endpoint, path));
        foreach (var header in headers) request.Headers.Add(header.Key, header.Value);
        if (protocol == Protocol.Responses) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", credentials.Key);
        else
        {
            request.Headers.Add("x-api-key", credentials.Key);
            if (!request.Headers.Contains("anthropic-version")) request.Headers.Add("anthropic-version", "2023-06-01");
        }
        return request;
    }

    private static async Task<JsonElement> ReadAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        try
        {
            await response.Content.LoadIntoBufferAsync(1024 * 1024, cancellationToken).ConfigureAwait(false);
            using var document = JsonDocument.Parse(await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false));
            return document.RootElement.Clone();
        }
        catch (JsonException) when (!response.IsSuccessStatusCode) { return default; }
        catch (JsonException) { throw ProviderProtocol.ProtocolError("the Files API returned invalid JSON"); }
    }

    private static ProviderException Rejected(HttpResponseMessage response, JsonElement body)
    {
        var error = ProviderErrors.Classify((ushort)response.StatusCode, body,
            response.Headers.TryGetValues("Retry-After", out var retry) ? retry.FirstOrDefault() : null);
        error.IsHttpRejection = true; error.Diagnostic = "files_api_rejected";
        return error;
    }

    private static string? Text(JsonObject node, string key) => node[key] is JsonValue value && value.TryGetValue<string>(out string? text) ? text : null;
}
