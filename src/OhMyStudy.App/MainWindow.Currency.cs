using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Infrastructure.Providers;

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
            try { using var http = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(10) }; return await CnyExchangeRates.GetAsync(http, _lifetime.Token); }
            catch { return null; }
        }
    }
    private static string RateNote(CnyRate rate) => $"汇率 {rate.Date}，1 美元 = {rate.Rate:0.####} 元" + (rate.IsStale ? "（缓存）" : "");
    private async Task UpdateModelCnyPreviewAsync()
    {
        if (ModelCnyPreview is null) return;
        long generation = ++_pricePreviewGeneration;
        double? input = double.TryParse(ModelPriceInput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var a) && double.IsFinite(a) && a >= 0 ? a : null;
        double? output = double.TryParse(ModelPriceOutput.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var b) && double.IsFinite(b) && b >= 0 ? b : null;
        if (input is null && output is null) { ModelCnyPreview.Text = ""; return; }
        if (SelectedCurrency.Length == 0) { ModelCnyPreview.Text = "请选择货币后查看人民币估算。"; return; }
        bool usd = SelectedCurrency == "USD";
        var rate = usd ? await GetCnyRateAsync() : null;
        if (_closed || generation != _pricePreviewGeneration) return;
        if (usd && rate is null) { ModelCnyPreview.Text = "暂时无法获取汇率，人民币估算不可用。原价格已保留。"; return; }
        double factor = rate?.Rate ?? 1;
        static string Price(double? value, double factor) => value.HasValue ? $"¥{value.Value * factor:0.######}" : "未设置";
        ModelCnyPreview.Text = $"人民币 / 百万 token：输入 {Price(input, factor)}    输出 {Price(output, factor)}" + (rate is not null ? "\n" + RateNote(rate) : "");
    }
    private async Task UpdateTaskCnyCostAsync(string jobId, long generation, string usage, double cost, string currency)
    {
        CnyRate? rate = currency == "USD" ? await GetCnyRateAsync() : null;
        if (_closed || generation != _taskRefreshGeneration || _selectedTaskId != jobId) return;
        TaskUsage.Text = usage + (currency == "CNY" ? $"预估费用 ¥{cost:0.######}" : currency == "USD" && rate is not null ? $"预估费用 ¥{cost * rate.Rate:0.######}（汇率 {rate.Date}）" + (rate.IsStale ? "（缓存）" : "") : "人民币估算暂不可用");
        ToolTipService.SetToolTip(TaskUsage, rate is not null ? RateNote(rate) : null);
    }
}
