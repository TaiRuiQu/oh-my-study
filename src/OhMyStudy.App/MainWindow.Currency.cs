using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Diagnostics;
using OhMyStudy.App.Diagnostics;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private Task<CnyRate?>? _exchangeRateTask;
    private DateOnly _exchangeRateDay;
    private CnyRate? _diagnosticRate;
    private long _pricePreviewGeneration;
    private void ModelPrice_Changed(object sender, TextChangedEventArgs e) { if (_initialized) _ = UpdateModelCnyPreviewAsync(); }
    private void ModelPriceCurrency_Changed(object sender, SelectionChangedEventArgs e) { if (_initialized) _ = UpdateModelCnyPreviewAsync(); }
    private Task<CnyRate?> GetCnyRateAsync()
    {
        if (_diagnosticRate is not null) return Task.FromResult<CnyRate?>(_diagnosticRate);
        if (Environment.GetCommandLineArgs().Any(a => a.Contains("smoke", StringComparison.Ordinal))) return Task.FromResult<CnyRate?>(null);
        var today = DateOnly.FromDateTime(DateTime.Now);
        if (_exchangeRateTask is null || _exchangeRateDay != today) { _exchangeRateDay = today; _exchangeRateTask = Read(); }
        return _exchangeRateTask;
        async Task<CnyRate?> Read()
        {
            try { using var http = new System.Net.Http.HttpClient(new DiagnosticHttpHandler(new System.Net.Http.SocketsHttpHandler { AllowAutoRedirect = false })) { Timeout = TimeSpan.FromSeconds(10) }; return await CnyExchangeRates.GetAsync(http, _lifetime.Token); }
            catch (Exception error) { AppLog.Error("Currency.Fetch", error); return null; }
        }
    }
    private static string RateNote(CnyRate rate) => UiText.Format("Exchange rate {0}: USD 1 = CNY {1:0.####}", rate.Date, rate.Rate) + (rate.IsStale ? UiText.Get(" (cached)") : "");
    private async Task UpdateModelCnyPreviewAsync()
    {
        if (ModelCnyPreview is null) return;
        long generation = ++_pricePreviewGeneration;
        double? input = double.TryParse(ModelPriceInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.IsFinite(a) && a >= 0 ? a : null;
        double? output = double.TryParse(ModelPriceOutput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && double.IsFinite(b) && b >= 0 ? b : null;
        double? read = double.TryParse(ModelPriceCacheRead.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var r) && double.IsFinite(r) && r >= 0 ? r : null;
        double? write = double.TryParse(ModelPriceCacheWrite.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && double.IsFinite(w) && w >= 0 ? w : null;
        if (input is null && output is null && read is null && write is null) { ModelCnyPreview.Text = ""; return; }
        if (SelectedCurrency.Length == 0) { ModelCnyPreview.Text = UiText.Get("Select a currency to see the CNY estimate."); return; }
        bool usd = SelectedCurrency == "USD";
        var rate = usd ? await GetCnyRateAsync() : null;
        if (_closed || generation != _pricePreviewGeneration) return;
        if (usd && rate is null) { ModelCnyPreview.Text = UiText.Get("Exchange rate unavailable. CNY estimate cannot be shown; original prices are preserved."); return; }
        double factor = rate?.Rate ?? 1;
        static string Price(double? value, double factor) => value.HasValue ? $"¥{value.Value * factor:0.######}" : UiText.Get("Not set");
        ModelCnyPreview.Text = UiText.Format("CNY estimate: uncached {0}    cached {1}    cache write {2}    output {3}", Price(input, factor), Price(read, factor), Price(write, factor), Price(output, factor)) + (rate is not null ? "\n" + RateNote(rate) : "");
    }
    private async Task UpdateTaskCnyCostAsync(string jobId, long generation, double cost, string currency)
    {
        CnyRate? rate = currency == "USD" ? await GetCnyRateAsync() : null;
        if (_closed || generation != _taskRefreshGeneration || _selectedTaskId != jobId) return;
        TaskUsage.Text = currency == "CNY" ? $"¥{cost:0.######}" : currency == "USD" && rate is not null ? $"¥{cost * rate.Rate:0.######}" : UiText.Get("Unavailable");
        TaskRateNote.Text = rate is not null ? RateNote(rate) : currency == "CNY" ? UiText.Get("Estimated using CNY prices.") : UiText.Get("Exchange rate unavailable. CNY estimate cannot be shown.");
        ToolTipService.SetToolTip(TaskUsage, rate is not null ? RateNote(rate) : null);
    }
}
