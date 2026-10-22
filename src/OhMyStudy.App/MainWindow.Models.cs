using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Providers;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private bool _refreshingProviders;
    private bool _modelRequestRunning;
    private string? _selectedModelId;
    private ModelConfig? _modelMetadataDraft;
    private string SelectedCurrency => ModelCurrency.SelectedIndex switch { 0 => "USD", 1 => "CNY", _ => "" };

    private async void ModelPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_modelSelectionSuppressed || ModelPicker.SelectedItem is not ModelConfig model) return;
        if (HasModelDraft)
        {
            _modelSelectionSuppressed = true;
            try { ModelPicker.SelectedItem = (ProviderPicker.SelectedItem as Provider)?.Models.FirstOrDefault(m => m.Id == _selectedModelId); }
            finally { _modelSelectionSuppressed = false; }
            if (!await ConfirmDiscardSettingsAsync(modelOnly: true)) return;
            _modelSelectionSuppressed = true;
            try { ModelPicker.SelectedItem = model; }
            finally { _modelSelectionSuppressed = false; }
        }
        PopulateModelEditor(model);
    }

    private void PopulateModelEditor(ModelConfig model)
    {
        _modelMetadataDraft = model;
        _selectedModelId = model.Id.Length == 0 ? null : model.Id;
        ModelDisplayName.Text = model.DisplayName ?? ""; ModelId.Text = model.Id;
        ModelContextWindow.Text = model.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? "";
        ModelVision.IsChecked = model.Capabilities.Vision; ModelTools.IsChecked = model.Capabilities.Tools;
        ModelPriceInput.Text = model.PriceInput?.ToString(CultureInfo.InvariantCulture) ?? "";
        ModelPriceCacheRead.Text = model.PriceCacheRead?.ToString(CultureInfo.InvariantCulture) ?? "";
        ModelPriceCacheWrite.Text = model.PriceCacheWrite?.ToString(CultureInfo.InvariantCulture) ?? "";
        ModelPriceOutput.Text = model.PriceOutput?.ToString(CultureInfo.InvariantCulture) ?? "";
        ModelCurrency.SelectedIndex = model.Currency switch { "CNY" => 1, "USD" => 0, _ => -1 }; ModelCatalogFeedback.Text = ""; ModelFeedback.Text = "";
        var provider = ProviderPicker.SelectedItem as Provider;
        string[] presets = provider?.Protocol switch
        {
            Protocol.Responses => ["none", "minimal", "low", "medium", "high", "xhigh", "max"],
            Protocol.Messages => ["low", "medium", "high", "max"],
            _ => []
        };
        var selected = model.Capabilities.Thinking.Select(t => t switch
        { ResponsesThinking v => v.Effort, AdaptiveThinking v => v.Effort, _ => "" })
            .Where(t => t.Length > 0).ToHashSet(StringComparer.Ordinal);
        ModelThinkingOptions.Children.Clear();
        foreach (string value in presets.Concat(selected).Distinct(StringComparer.Ordinal))
            ModelThinkingOptions.Children.Add(new CheckBox { Content = value, IsChecked = selected.Contains(value), MinWidth = 96, Margin = new Thickness(0, 0, 16, 0) });
        ModelThinkingBudget.Visibility = provider?.Protocol == Protocol.Messages ? Visibility.Visible : Visibility.Collapsed;
        ModelThinkingBudget.Text = model.Capabilities.Thinking.FirstOrDefault(t => t is BudgetThinking) switch
        { BudgetThinking v => v.Tokens.ToString(CultureInfo.InvariantCulture), _ => "" };
        ModelExtraInfo.Text = ExtraModelInfo(model);
        _ = UpdateModelCnyPreviewAsync();
        ModelEditor.Visibility = Visibility.Visible;
        _modelFormBaseline = ModelFormState();
        UpdateSettingsActionAvailability();
    }

    private async void NewModel_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderPicker.SelectedItem is not Provider) { ModelFeedback.Text = UiText.Get("请先保存供应商。"); return; }
        if (!await ConfirmDiscardSettingsAsync(modelOnly: true)) return;
        ModelPicker.SelectedIndex = -1;
        PopulateModelEditor(new() { Id = "", Capabilities = Capabilities.Unknown, Currency = "USD" });
        ModelId.Focus(FocusState.Programmatic);
    }

    private async void SaveModel_Click(object sender, RoutedEventArgs e) => await SaveModelAsync();

    private async Task SaveModelAsync()
    {
        var library = _library;
        if (_modelRequestRunning || library is null || ProviderPicker.SelectedItem is not Provider provider) return;
        _modelRequestRunning = true; SettingsPanel.IsEnabled = false;
        try
        {
            static double? Price(string text) => string.IsNullOrWhiteSpace(text) ? null : double.Parse(text, CultureInfo.InvariantCulture);
            var thinking = new List<Thinking>();
            foreach (var option in ModelThinkingOptions.Children.OfType<CheckBox>().Where(c => c.IsChecked == true))
            {
                string value = (string)option.Content;
                thinking.Add(provider.Protocol switch
                { Protocol.Responses => new ResponsesThinking { Effort = value }, Protocol.Messages => new AdaptiveThinking { Effort = value }, _ => throw new ArgumentException() });
            }
            if (provider.Protocol == Protocol.Messages && !string.IsNullOrWhiteSpace(ModelThinkingBudget.Text))
            {
                ulong budget = ulong.Parse(ModelThinkingBudget.Text, CultureInfo.InvariantCulture);
                if (budget < 1024) throw new ArgumentException();
                thinking.Add(new BudgetThinking { Tokens = budget });
            }
            var old = provider.Models.FirstOrDefault(m => m.Id == _selectedModelId);
            var model = (_modelMetadataDraft ?? old ?? new ModelConfig { Id = "", Capabilities = Capabilities.Unknown, Currency = "USD" }) with
            {
                Id = ModelId.Text.Trim(),
                DisplayName = ModelDisplayName.Text.Trim(),
                ContextWindow = string.IsNullOrWhiteSpace(ModelContextWindow.Text) ? null : ulong.Parse(ModelContextWindow.Text, CultureInfo.InvariantCulture),
                PriceInput = Price(ModelPriceInput.Text),
                PriceCacheRead = Price(ModelPriceCacheRead.Text),
                PriceCacheWrite = Price(ModelPriceCacheWrite.Text),
                PriceOutput = Price(ModelPriceOutput.Text),
                Currency = SelectedCurrency,
                Capabilities = (_modelMetadataDraft?.Capabilities ?? old?.Capabilities ?? Capabilities.Unknown) with
                { Vision = ModelVision.IsChecked, Tools = ModelTools.IsChecked, Thinking = thinking, MessagesBudget = thinking.Any(t => t is BudgetThinking) || _modelMetadataDraft?.Capabilities.MessagesBudget == true, Source = "manual" }
            };
            var models = provider.Models.ToList();
            int index = models.FindIndex(m => m.Id == _selectedModelId);
            if (models.Any(m => m.Id == model.Id && m.Id != _selectedModelId)) throw new ArgumentException();
            if (index < 0) models.Add(model); else models[index] = model;
            await library.UpsertProviderAsync(provider with { Models = models }, _lifetime.Token);
            if (_settings.DefaultModel is null || _settings.DefaultModel == provider.Id + ":" + _selectedModelId)
                await ChangeSettingsAsync(current => current with { DefaultModel = provider.Id + ":" + model.Id });
            _selectedModelId = model.Id;
            UpdateProviderEntry(provider with { Models = models }); UpdateModelList(provider with { Models = models }); ModelFeedback.Text = UiText.Get("模型已保存。");
        }
        catch { ModelFeedback.Text = UiText.Get("未能保存。请检查模型 ID，选择货币，并确认窗口和预算为整数、价格为非负数。"); }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; }
    }

    private async void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        var library = _library;
        if (library is null || ProviderPicker.SelectedItem is not Provider provider || _selectedModelId is not { } id) return;
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = UiText.Get("删除模型？"), Content = UiText.Get("已有资料与整理结果会保留。"), PrimaryButtonText = UiText.Get("删除"), CloseButtonText = UiText.Get("取消") };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _modelRequestRunning = true; SettingsPanel.IsEnabled = false;
        try
        {
            await library.UpsertProviderAsync(provider with { Models = provider.Models.Where(m => m.Id != id).ToList() }, _lifetime.Token);
            if (_settings.DefaultModel == provider.Id + ":" + id) await ChangeSettingsAsync(current => current with { DefaultModel = null });
            var updated = provider with { Models = provider.Models.Where(m => m.Id != id).ToList() };
            _selectedModelId = null; UpdateProviderEntry(updated); UpdateModelList(updated); ModelFeedback.Text = UiText.Get("模型已删除。");
        }
        catch { ModelFeedback.Text = UiText.Get("删除未完成，请重试。"); }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; }
    }

    private async void TestProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_modelRequestRunning) return;
        if (ProviderPicker.SelectedItem is not Provider provider || ModelPicker.SelectedItem is not ModelConfig model)
        { ModelFeedback.Text = UiText.Get("请选择已保存的模型。"); return; }
        _modelRequestRunning = true; SettingsPanel.IsEnabled = false; byte[]? key = null;
        try
        {
            key = WindowsCredentialStore.Read(provider.KeyRef ?? "");
            if (key is null) { ModelFeedback.Text = UiText.Get("请先保存密钥。"); return; }
            ModelFeedback.Text = UiText.Get("正在测试连接…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = new ProviderClient(provider.Protocol, provider.BaseUrl);
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await client.TestConnectionAsync(new(Encoding.UTF8.GetString(key)), model.Id, timeout.Token);
            if (ProviderPicker.SelectedItem is Provider current && current.Id == provider.Id)
                ModelFeedback.Text = UiText.Format("连接成功（{0}，{1:F1} 秒）", model.Label, watch.Elapsed.TotalSeconds);
        }
        catch (Exception error)
        {
            AppLog.Error("Provider.TestConnection", error);
            if (!_closed) ModelFeedback.Text = UiText.Get("连接失败，请检查已保存的地址、密钥、模型及网络。");
        }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private async void FillModelCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_modelRequestRunning || string.IsNullOrWhiteSpace(ModelId.Text)) return;
        string requestedId = ModelId.Text.Trim(); var provider = ProviderPicker.SelectedItem as Provider; _modelRequestRunning = true;
        SettingsPanel.IsEnabled = false;
        try
        {
            ModelCatalogFeedback.Text = UiText.Get("正在获取…");
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var http = new System.Net.Http.HttpClient();
            var catalog = await ModelCatalog.GetAsync(http, timeout.Token);
            var matches = ModelCatalog.Find(catalog.Value, requestedId);
            if (requestedId != ModelId.Text.Trim() || provider?.Id != _selectedProviderId) return;
            if (matches.Count == 0) { ModelCatalogFeedback.Text = UiText.Get("未找到相同 ID，请手动填写。"); return; }
            var choice = new ComboBox { Header = UiText.Get("信息来源"), HorizontalAlignment = HorizontalAlignment.Stretch, Width = Math.Max(200, Math.Min(460, RootGrid.ActualWidth - 96)) };
            foreach (var match in matches) choice.Items.Add(new TextBlock { Text = match.ProviderName, MaxWidth = choice.Width - 48, TextTrimming = TextTrimming.CharacterEllipsis });
            choice.SelectedIndex = Math.Max(0, matches.FindIndex(m => m.Api is not null && provider is not null
                && Uri.TryCreate(m.Api, UriKind.Absolute, out var api) && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var address) && api.Host == address.Host));
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = UiText.Get("按模型 ID 精确匹配。不同供应商的价格可能不同，实际费用以供应商账单为准。"), TextWrapping = TextWrapping.Wrap }); content.Children.Add(choice);
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = UiText.Get("填充模型信息"), Content = content, PrimaryButtonText = UiText.Get("填充"), CloseButtonText = UiText.Get("取消") };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { ModelCatalogFeedback.Text = ""; return; }
            var selected = matches[choice.SelectedIndex];
            ApplyCatalogModel(ModelCatalog.ForProtocol(selected.Model, provider!.Protocol));
            ModelCatalogFeedback.Text = UiText.Format("来自 models.dev / {0}（{1:yyyy/MM/dd}）", selected.ProviderName, catalog.FetchedAt.ToLocalTime()) + (catalog.IsStale ? UiText.Get("，当前使用缓存") : "") + UiText.Get("，尚未保存");

        }
        catch { ModelCatalogFeedback.Text = UiText.Get("暂时无法获取，可手动填写，明天可再次更新。"); }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; }
    }
    private void ApplyCatalogModel(ModelConfig model)
    {
        _modelMetadataDraft = model with
        {
            MaxOutputTokens = model.MaxOutputTokens ?? _modelMetadataDraft?.MaxOutputTokens,
            InputModalities = model.InputModalities ?? _modelMetadataDraft?.InputModalities,
            OutputModalities = model.OutputModalities ?? _modelMetadataDraft?.OutputModalities,
            Reasoning = model.Reasoning ?? _modelMetadataDraft?.Reasoning
        };
        if (model.DisplayName is not null) ModelDisplayName.Text = model.DisplayName;
        if (model.ContextWindow.HasValue) ModelContextWindow.Text = model.ContextWindow.Value.ToString(CultureInfo.InvariantCulture);
        if (model.Capabilities.Vision.HasValue) ModelVision.IsChecked = model.Capabilities.Vision;
        if (model.Capabilities.Tools.HasValue) ModelTools.IsChecked = model.Capabilities.Tools;
        // Missing metadata never clears a confirmed user value.
        bool hasPrices = model.PriceInput.HasValue || model.PriceOutput.HasValue || model.PriceCacheRead.HasValue || model.PriceCacheWrite.HasValue;
        if (hasPrices && SelectedCurrency != "USD") { ModelPriceInput.Text = ""; ModelPriceOutput.Text = ""; ModelPriceCacheRead.Text = ""; ModelPriceCacheWrite.Text = ""; }
        if (model.PriceInput.HasValue) ModelPriceInput.Text = model.PriceInput.Value.ToString(CultureInfo.InvariantCulture);
        if (model.PriceCacheRead.HasValue) ModelPriceCacheRead.Text = model.PriceCacheRead.Value.ToString(CultureInfo.InvariantCulture);
        if (model.PriceCacheWrite.HasValue) ModelPriceCacheWrite.Text = model.PriceCacheWrite.Value.ToString(CultureInfo.InvariantCulture);
        if (model.PriceOutput.HasValue) ModelPriceOutput.Text = model.PriceOutput.Value.ToString(CultureInfo.InvariantCulture);
        if (hasPrices) ModelCurrency.SelectedIndex = 0;
        if (model.Reasoning == false || model.Capabilities.Thinking.Count > 0)
        {
            var efforts = model.Capabilities.Thinking.Select(t => t switch { ResponsesThinking r => r.Effort, AdaptiveThinking a => a.Effort, _ => "" }).ToHashSet();
            foreach (var check in ModelThinkingOptions.Children.OfType<CheckBox>()) check.IsChecked = efforts.Contains((string)check.Content);
        }
        ModelExtraInfo.Text = ExtraModelInfo(model); _ = UpdateModelCnyPreviewAsync();
    }
    private static string ExtraModelInfo(ModelConfig model)
    {
        static string Modes(IEnumerable<string>? values) => string.Join("、", (values ?? []).Select(v => v switch { "text" => UiText.Get("文字"), "image" => UiText.Get("图片"), "audio" => UiText.Get("音频"), "video" => UiText.Get("视频"), "pdf" => "PDF", _ => v }));
        var info = new List<string>();
        if (model.InputModalities is { Count: > 0 }) info.Add(UiText.Get("可读：") + Modes(model.InputModalities));
        if (model.OutputModalities is { Count: > 0 }) info.Add(UiText.Get("可生成：") + Modes(model.OutputModalities));
        if (model.MaxOutputTokens is { } max) info.Add(UiText.Format("最多输出 {0:N0} token", max));
        if (model.Reasoning.HasValue) info.Add(model.Reasoning.Value ? UiText.Get("支持思考") : UiText.Get("不支持思考"));
        return string.Join("；", info);
    }

}
