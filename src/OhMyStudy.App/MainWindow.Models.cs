using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Providers;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private bool _refreshingProviders;
    private bool _modelRequestRunning;
    private string? _selectedModelId;

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
        _selectedModelId = model.Id.Length == 0 ? null : model.Id;
        ModelDisplayName.Text = model.DisplayName ?? ""; ModelId.Text = model.Id;
        ModelContextWindow.Text = model.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? "";
        ModelVision.IsChecked = model.Capabilities.Vision; ModelTools.IsChecked = model.Capabilities.Tools;
        ModelPriceInput.Text = model.PriceInput?.ToString(CultureInfo.InvariantCulture) ?? "";
        ModelPriceOutput.Text = model.PriceOutput?.ToString(CultureInfo.InvariantCulture) ?? "";
        ModelCurrency.Text = model.Currency; ModelCatalogFeedback.Text = ""; ModelFeedback.Text = "";
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
        ModelEditor.Visibility = Visibility.Visible;
        _modelFormBaseline = ModelFormState();
        UpdateSettingsActionAvailability();
    }

    private async void NewModel_Click(object sender, RoutedEventArgs e)
    {
        if (ProviderPicker.SelectedItem is not Provider) { ModelFeedback.Text = "请先保存供应商。"; return; }
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
            var model = new ModelConfig
            {
                Id = ModelId.Text.Trim(),
                DisplayName = ModelDisplayName.Text.Trim(),
                ContextWindow = string.IsNullOrWhiteSpace(ModelContextWindow.Text) ? null : ulong.Parse(ModelContextWindow.Text, CultureInfo.InvariantCulture),
                PriceInput = Price(ModelPriceInput.Text),
                PriceOutput = Price(ModelPriceOutput.Text),
                Currency = ModelCurrency.Text.Trim(),
                Capabilities = (old?.Capabilities ?? Capabilities.Unknown) with
                { Vision = ModelVision.IsChecked, Tools = ModelTools.IsChecked, Thinking = thinking, MessagesBudget = thinking.Any(t => t is BudgetThinking), Source = "manual" }
            };
            var models = provider.Models.ToList();
            int index = models.FindIndex(m => m.Id == _selectedModelId);
            if (models.Any(m => m.Id == model.Id && m.Id != _selectedModelId)) throw new ArgumentException();
            if (index < 0) models.Add(model); else models[index] = model;
            await library.UpsertProviderAsync(provider with { Models = models }, _lifetime.Token);
            if (_settings.DefaultModel is null || _settings.DefaultModel == provider.Id + ":" + _selectedModelId)
                await ChangeSettingsAsync(current => current with { DefaultModel = provider.Id + ":" + model.Id });
            _selectedModelId = model.Id;
            UpdateProviderEntry(provider with { Models = models }); UpdateModelList(provider with { Models = models }); ModelFeedback.Text = "模型已保存。";
        }
        catch { ModelFeedback.Text = "未能保存。请检查模型 ID 是否重复、窗口和预算是否为整数、价格是否为非负数。"; }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; }
    }

    private async void DeleteModel_Click(object sender, RoutedEventArgs e)
    {
        var library = _library;
        if (library is null || ProviderPicker.SelectedItem is not Provider provider || _selectedModelId is not { } id) return;
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "删除模型？", Content = "已有资料与整理结果会保留。", PrimaryButtonText = "删除", CloseButtonText = "取消" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _modelRequestRunning = true; SettingsPanel.IsEnabled = false;
        try
        {
            await library.UpsertProviderAsync(provider with { Models = provider.Models.Where(m => m.Id != id).ToList() }, _lifetime.Token);
            if (_settings.DefaultModel == provider.Id + ":" + id) await ChangeSettingsAsync(current => current with { DefaultModel = null });
            var updated = provider with { Models = provider.Models.Where(m => m.Id != id).ToList() };
            _selectedModelId = null; UpdateProviderEntry(updated); UpdateModelList(updated); ModelFeedback.Text = "模型已删除。";
        }
        catch { ModelFeedback.Text = "删除未完成，请重试。"; }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; }
    }

    private async void TestProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_modelRequestRunning) return;
        if (ProviderPicker.SelectedItem is not Provider provider || ModelPicker.SelectedItem is not ModelConfig model)
        { ModelFeedback.Text = "请选择已保存的模型。"; return; }
        _modelRequestRunning = true; SettingsPanel.IsEnabled = false; byte[]? key = null;
        try
        {
            key = WindowsCredentialStore.Read(provider.KeyRef ?? "");
            if (key is null) { ModelFeedback.Text = "请先保存密钥。"; return; }
            ModelFeedback.Text = "正在测试连接…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var client = new ProviderClient(provider.Protocol, provider.BaseUrl);
            var options = new RequestOptions { Model = Model.Manual(model.Id), MaxOutputTokens = 256, Stream = false, AllowUnknownCapabilities = true, Extra = [] };
            var watch = System.Diagnostics.Stopwatch.StartNew();
            await client.CallWithRetryAsync(new(Encoding.UTF8.GetString(key)), options, ProviderProtocol.Initial(provider.Protocol, "Reply with OK.", []), [],
                new ProviderRetryPolicy { MaximumAttempts = 1 }, cancellationToken: timeout.Token);
            if (ProviderPicker.SelectedItem is Provider current && current.Id == provider.Id)
                ModelFeedback.Text = $"连接成功 · {model.Label} · {watch.Elapsed.TotalSeconds:F1} 秒";
        }
        catch { if (!_closed) ModelFeedback.Text = "连接失败，请检查已保存的地址、密钥、模型及网络。"; }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private async void FillModelCatalog_Click(object sender, RoutedEventArgs e)
    {
        if (_modelRequestRunning || string.IsNullOrWhiteSpace(ModelId.Text)) return;
        string requestedId = ModelId.Text.Trim(); var provider = ProviderPicker.SelectedItem as Provider; _modelRequestRunning = true;
        SettingsPanel.IsEnabled = false;
        try
        {
            ModelCatalogFeedback.Text = "正在获取…";
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(30));
            using var http = new System.Net.Http.HttpClient();
            var matches = await ModelCatalog.FindAsync(http, requestedId, timeout.Token);
            if (requestedId != ModelId.Text.Trim() || provider?.Id != _selectedProviderId) return;
            if (matches.Count == 0) { ModelCatalogFeedback.Text = "未找到相同 ID，请手动填写。"; return; }
            var choice = new ComboBox { Header = "价格来源", HorizontalAlignment = HorizontalAlignment.Stretch };
            foreach (var match in matches) choice.Items.Add(match.ProviderName);
            choice.SelectedIndex = Math.Max(0, matches.FindIndex(m => m.Api is not null && provider is not null
                && Uri.TryCreate(m.Api, UriKind.Absolute, out var api) && Uri.TryCreate(provider.BaseUrl, UriKind.Absolute, out var address) && api.Host == address.Host));
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = "按模型 ID 精确匹配。不同供应商的价格可能不同，实际费用以供应商账单为准。", TextWrapping = TextWrapping.Wrap }); content.Children.Add(choice);
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "填充模型信息", Content = content, PrimaryButtonText = "填充", CloseButtonText = "取消" };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) { ModelCatalogFeedback.Text = ""; return; }
            var selected = matches[choice.SelectedIndex];
            ModelDisplayName.Text = selected.Model.DisplayName ?? ""; ModelContextWindow.Text = selected.Model.ContextWindow?.ToString(CultureInfo.InvariantCulture) ?? "";
            ModelVision.IsChecked = selected.Model.Capabilities.Vision; ModelTools.IsChecked = selected.Model.Capabilities.Tools;
            ModelPriceInput.Text = selected.Model.PriceInput?.ToString(CultureInfo.InvariantCulture) ?? "";
            ModelPriceOutput.Text = selected.Model.PriceOutput?.ToString(CultureInfo.InvariantCulture) ?? ""; ModelCurrency.Text = selected.Model.Currency;
            ModelCatalogFeedback.Text = $"来自 models.dev / {selected.ProviderName}，尚未保存；思考选项需自行确认。";
        }
        catch { ModelCatalogFeedback.Text = "获取失败，可手动填写或稍后重试。"; }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; }
    }
}
