using System.Globalization;
using System.Net.Http;
using System.Text.Json;

namespace OhMyStudy.Infrastructure.Providers;

public sealed record CnyRate(double Rate, string Date, bool IsStale);
public static class CnyExchangeRates
{
    private static readonly DailyJsonFeed Feed = new("https://api.frankfurter.dev/v2/rate/USD/CNY", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMyStudy", "Cache", "usd-cny.json"), 65536, Valid);
    public static async Task<CnyRate> GetAsync(HttpClient http, CancellationToken ct = default)
    {
        var cached = await Feed.GetAsync(http, ct).ConfigureAwait(false);
        return new(cached.Value.GetProperty("rate").GetDouble(), cached.Value.GetProperty("date").GetString()!, cached.IsStale);
    }
    private static bool Valid(JsonElement data) => data.ValueKind == JsonValueKind.Object
        && data.TryGetProperty("base", out var b) && b.GetString()?.Equals("USD", StringComparison.OrdinalIgnoreCase) == true
        && data.TryGetProperty("quote", out var q) && q.GetString()?.Equals("CNY", StringComparison.OrdinalIgnoreCase) == true
        && data.TryGetProperty("rate", out var r) && r.ValueKind == JsonValueKind.Number && r.TryGetDouble(out var value) && double.IsFinite(value) && value > 0
        && data.TryGetProperty("date", out var d) && d.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(d.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out _);
}
