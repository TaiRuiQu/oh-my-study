using Microsoft.UI.Xaml;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task RunBillingSmokeAsync(string reportPath)
    {
        string root = Path.Combine(Path.GetDirectoryName(reportPath)!, "billing-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); _libraryPointerOverride = Path.Combine(root, "active-library.txt");
        var checks = new List<string>(); int exit = 1;
        try
        {
            _diagnosticRate = new(7.12, "2026-10-20", false);
            Require(await InitializeLibraryAsync(Path.Combine(root, "library")), "isolated-library");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            RootGrid.RequestedTheme = ElementTheme.Light;
            Navigation.SelectedItem = Navigation.SettingsItem; await Task.Delay(250);
            await CheckModelPricingAsync(Require, Capture);
            exit = 0;
        }
        catch (Exception error) { AppLog.Error("BillingSmoke", error); checks.Add("FAIL " + error); }
        finally
        {
            await File.WriteAllLinesAsync(reportPath, checks.Prepend(exit == 0 ? "PASS" : "FAIL"));
            if (_jobs is not null) await _jobs.DisposeAsync(); _jobs = null;
            if (_library is not null) await _library.DisposeAsync(); _library = null;
            _shutdownFinished = true; Microsoft.UI.Xaml.Application.Current.Exit(); Environment.Exit(exit);
        }
        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        async Task Capture(string name) { await Task.Delay(500); RootGrid.UpdateLayout(); await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + ".png")); }
    }

    private async Task<ModelConfig> CheckModelPricingAsync(Action<bool, string> Require, Func<string, Task> Capture)
    {
        var model = new ModelConfig { Id = "study-model", DisplayName = "示例模型", ContextWindow = 200000, PriceInput = 1.25, PriceCacheRead = 0.125, PriceCacheWrite = 1.5625, PriceOutput = 5, Currency = "USD", Capabilities = new() { Vision = true, Tools = true, Thinking = [new ResponsesThinking { Effort = "high" }] }, MaxOutputTokens = 32000, Reasoning = true, InputModalities = ["text", "image", "pdf"], OutputModalities = ["text"] };
        var provider = new Provider { Id = Guid.NewGuid().ToString(), Name = "示例供应商", Protocol = Protocol.Responses, BaseUrl = "https://synthetic.invalid/v1", KeyRef = null, Models = [model] };
        await _library!.UpsertProviderAsync(provider); await RefreshProvidersAsync(); ProviderPicker.SelectedItem = _providers.Single(); PopulateModelEditor(model);
        await UpdateModelCnyPreviewAsync(); RootGrid.UpdateLayout(); SettingsPanel.UpdateLayout();
        double priceY = ModelPriceFields.TransformToVisual((UIElement)SettingsPanel.Content).TransformPoint(new(0, 0)).Y;
        SettingsPanel.ChangeView(null, Math.Max(0, priceY - 100), null, true); await Capture("model-usd"); Require(ModelCnyPreview.Text.Contains("8.9"), "usd-cny-preview");
        double rowY = ModelPriceInput.TransformToVisual(ModelPriceFields).TransformPoint(new(0, 0)).Y;
        Require(new FrameworkElement[] { ModelPriceCacheRead, ModelPriceCacheWrite, ModelPriceOutput, ModelCurrency }.All(field => Math.Abs(field.TransformToVisual(ModelPriceFields).TransformPoint(new(0, 0)).Y - rowY) < 1), "four-prices-and-currency-share-one-row");
        Require(!ModelCnyPreview.Text.Contains("token", StringComparison.OrdinalIgnoreCase), "price-preview-omits-unit");
        Require(ModelCnyPreview.Text.Contains("命中 ¥0.89") && ModelCnyPreview.Text.Contains("缓存写入 ¥11.125"), "cache-cny-preview");
        RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("model-prices-dark"); RootGrid.RequestedTheme = ElementTheme.Light;
        ModelCurrency.SelectedIndex = 1; await UpdateModelCnyPreviewAsync(); await Capture("model-cny"); Require(ModelCnyPreview.Text.Contains("¥1.25"), "cny-no-conversion");
        await SaveModelAsync();
        var savedModel = (await _library.ListProvidersAsync()).Single().Models.Single();
        Require(savedModel.PriceCacheRead == 0.125 && savedModel.PriceCacheWrite == 1.5625, "cache-prices-save-and-reload");
        ModelPriceCacheRead.Text = "0.25"; Require(HasModelDraft, "cache-read-draft-detected"); PopulateModelEditor(savedModel);
        ModelPriceCacheWrite.Text = "2"; Require(HasModelDraft, "cache-write-draft-detected"); PopulateModelEditor(savedModel);
        ModelPriceCacheWrite.Text = "-1"; await SaveModelAsync(); await Capture("model-prices-error");
        Require(ModelFeedback.Text.StartsWith("未能保存") && (await _library.ListProvidersAsync()).Single().Models.Single().PriceCacheWrite == 1.5625, "invalid-cache-price-keeps-saved-value");
        PopulateModelEditor(model with { PriceInput = null, PriceCacheRead = null, PriceCacheWrite = null, PriceOutput = null });
        await UpdateModelCnyPreviewAsync(); await Capture("model-prices-empty"); Require(ModelCnyPreview.Visibility == Visibility.Collapsed, "empty-prices-have-no-preview");
        RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("model-prices-empty-dark"); RootGrid.RequestedTheme = ElementTheme.Light;
        PopulateModelEditor(savedModel);
        ApplyCatalogModel(model with { PriceInput = null, PriceOutput = null, PriceCacheWrite = null });
        Require(SelectedCurrency == "USD" && ModelPriceInput.Text == "" && ModelPriceOutput.Text == "" && ModelPriceCacheWrite.Text == "" && ModelPriceCacheRead.Text == "0.125", "catalog-currency-change-clears-other-currency-prices");
        PopulateModelEditor(model);
        ApplyCatalogModel(model with { PriceInput = null, PriceOutput = null, PriceCacheRead = null, PriceCacheWrite = null });
        Require(ModelPriceCacheRead.Text == "0.125" && ModelPriceCacheWrite.Text == "1.5625", "catalog-missing-cache-prices-preserves-user-values");
        PopulateModelEditor(model with { PriceInput = null, PriceCacheWrite = null, PriceOutput = null });
        await UpdateModelCnyPreviewAsync(); await Capture("model-prices-partial");
        PopulateModelEditor(savedModel);
        Require(ModelMaxTokens.Text.Length == 0, "catalog-limit-does-not-become-user-output-setting");
        ModelMaxTokens.Text = "65536"; Require(HasModelDraft, "max-tokens-draft-detected");
        await SaveModelAsync();
        var customTokens = (await _library.ListProvidersAsync()).Single().Models.Single();
        Require(customTokens.MaxTokens == 65536 && customTokens.MaxOutputTokens == 32000 && ModelMaxTokens.Text == "65536", "max-tokens-save-reload-preserves-catalog-metadata");
        ApplyCatalogModel(model with { MaxOutputTokens = 8192 });
        Require(ModelMaxTokens.Text == "65536", "catalog-fill-preserves-user-output-limit");
        PopulateModelEditor(customTokens);
        foreach (string invalid in new[] { "0", "-1", "1.5", "2147483648", "invalid" })
        {
            ModelMaxTokens.Text = invalid; await SaveModelAsync();
            Require((await _library.ListProvidersAsync()).Single().Models.Single().MaxTokens == 65536 && ModelFeedback.Text.Contains("max_tokens", StringComparison.Ordinal), "invalid-max-tokens-keeps-saved-value-" + invalid);
        }
        PopulateModelEditor(customTokens);
        ModelMaxTokens.StartBringIntoView(); await Capture("max-tokens-wide");
        AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Task.Delay(250);
        ModelMaxTokens.StartBringIntoView(); await Capture("max-tokens-compact");
        RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("max-tokens-compact-dark"); RootGrid.RequestedTheme = ElementTheme.Light;
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960)); await Task.Delay(250);
        ModelMaxTokens.Text = ""; await SaveModelAsync();
        Require((await _library.ListProvidersAsync()).Single().Models.Single().MaxTokens is null, "blank-max-tokens-restores-automatic-limit");
        PopulateModelEditor(savedModel);
        _modelFormBaseline = ModelFormState();
        return model;
    }
}
