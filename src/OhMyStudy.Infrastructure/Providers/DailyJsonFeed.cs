using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace OhMyStudy.Infrastructure.Providers;

public sealed record CachedJson(JsonElement Value, DateTimeOffset FetchedAt, bool IsStale);

/// <summary>One bounded fetch per local day, durable last-good data, atomic replacement.</summary>
public sealed class DailyJsonFeed(string url, string cacheFile, int maximumBytes, Func<JsonElement, bool> validate, TimeProvider? clock = null)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly TimeProvider time = clock ?? TimeProvider.System;
    public async Task<CachedJson> GetAsync(HttpClient http, CancellationToken ct = default)
    {
        await gate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            var now = time.GetUtcNow(); string day = now.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
            CachedJson? cached = null;
            try
            {
                var file = new FileInfo(cacheFile);
                if (file.Exists && file.Length <= maximumBytes + 1024)
                {
                    using var json = JsonDocument.Parse(await File.ReadAllBytesAsync(cacheFile, ct).ConfigureAwait(false));
                    var stamp = json.RootElement.GetProperty("fetched_at").GetDateTimeOffset();
                    var data = json.RootElement.GetProperty("data");
                    if (stamp <= now && validate(data)) cached = new(data.Clone(), stamp, stamp.ToLocalTime().ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) != day);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or FormatException or InvalidOperationException) { }
            if (cached is { IsStale: false }) return cached;
            string attemptFile = cacheFile + ".attempt";
            if (File.Exists(attemptFile) && (await File.ReadAllTextAsync(attemptFile, ct).ConfigureAwait(false)) == day)
                return cached ?? throw new IOException("今日获取未完成，请稍后或明天重试。");
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cacheFile))!);
            await File.WriteAllTextAsync(attemptFile, day, ct).ConfigureAwait(false);
            try
            {
                using var response = await http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                if (response.Content.Headers.ContentLength > maximumBytes) throw new InvalidDataException("Response exceeds limit");
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var bytes = new MemoryStream(); byte[] buffer = new byte[16384];
                while (true)
                {
                    int count = await stream.ReadAsync(buffer, ct).ConfigureAwait(false);
                    if (count == 0) break;
                    if (bytes.Length + count > maximumBytes) throw new InvalidDataException("Response exceeds limit");
                    bytes.Write(buffer, 0, count);
                }
                using var data = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length));
                if (!validate(data.RootElement)) throw new InvalidDataException("Invalid response");
                string temporary = cacheFile + "." + Guid.NewGuid().ToString("N") + ".tmp";
                try
                {
                    await using (var file = File.Create(temporary))
                    {
                        using var writer = new Utf8JsonWriter(file); writer.WriteStartObject(); writer.WriteString("fetched_at", now); writer.WritePropertyName("data"); data.RootElement.WriteTo(writer); writer.WriteEndObject(); await writer.FlushAsync(ct).ConfigureAwait(false);
                    }
                    File.Move(temporary, cacheFile, true);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
                return new(data.RootElement.Clone(), now, false);
            }
            catch (Exception error) when (cached is not null && !ct.IsCancellationRequested && error is HttpRequestException or IOException or JsonException or TaskCanceledException) { return cached; }
        }
        finally { gate.Release(); }
    }
}
