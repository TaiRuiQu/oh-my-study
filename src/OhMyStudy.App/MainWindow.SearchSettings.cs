using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Application.Diagnostics;
using OhMyStudy.App.Diagnostics;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private string _searchSettingsBaseline = "";
    private bool _savingSearchSettings;
    private string SearchSettingsState() => string.Join('\u001f', EmbeddingBaseUrl.Text, EmbeddingModelName.Text, RerankerBaseUrl.Text, RerankerModelName.Text,
        EmbeddingApiType.SelectedIndex, RerankerApiType.SelectedIndex, RemoveEmbeddingKey.IsChecked, RemoveRerankerKey.IsChecked);
    private bool HasSearchSettingsDraft => _settingsFormsReady && (SearchSettingsState() != _searchSettingsBaseline || EmbeddingKey.Password.Length > 0 || RerankerKey.Password.Length > 0);

    private void PopulateSearchSettings()
    {
        EmbeddingApiType.SelectedIndex = _settings.EmbeddingModel?.ApiType == "dashscope" ? 1 : 0;
        RerankerApiType.SelectedIndex = _settings.RerankerModel?.ApiType == "dashscope" ? 1 : 0;
        EmbeddingBaseUrl.Text = _settings.EmbeddingModel?.BaseUrl ?? "";
        EmbeddingModelName.Text = _settings.EmbeddingModel?.Model ?? "";
        RerankerBaseUrl.Text = _settings.RerankerModel?.BaseUrl ?? "";
        RerankerModelName.Text = _settings.RerankerModel?.Model ?? "";
        EmbeddingKey.Password = RerankerKey.Password = "";
        EmbeddingKey.PlaceholderText = UiText.Get(_settings.EmbeddingModel?.KeyRef is null ? "Optional for local services" : "Saved key; leave blank to keep");
        RerankerKey.PlaceholderText = UiText.Get(_settings.RerankerModel?.KeyRef is null ? "Optional for local services" : "Saved key; leave blank to keep");
        RemoveEmbeddingKey.IsChecked = RemoveRerankerKey.IsChecked = false;
        _searchSettingsBaseline = SearchSettingsState();
        UpdateSearchApiPlaceholders();
    }

    private void SearchApiType_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateSearchApiPlaceholders();

    private void UpdateSearchApiPlaceholders()
    {
        if (EmbeddingBaseUrl is null || RerankerBaseUrl is null) return;
        const string dashscope = "https://<workspace-id>.cn-beijing.maas.aliyuncs.com/api/v1";
        EmbeddingBaseUrl.PlaceholderText = EmbeddingApiType.SelectedIndex == 1 ? dashscope : "https://api.openai.com/v1";
        RerankerBaseUrl.PlaceholderText = RerankerApiType.SelectedIndex == 1 ? dashscope : "https://api.example.com/v1";
        EmbeddingModelName.PlaceholderText = EmbeddingApiType.SelectedIndex == 1 ? "text-embedding-v4" : "text-embedding-3-small";
        RerankerModelName.PlaceholderText = RerankerApiType.SelectedIndex == 1 ? "qwen3-rerank" : "";
    }

    private void SearchModelFields_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        var grid = (Grid)sender;
        bool stacked = e.NewSize.Width < 680;
        int rows = stacked ? 5 : 2;
        if (grid.RowDefinitions.Count != rows)
        {
            grid.RowDefinitions.Clear();
            for (int index = 0; index < rows; index++) grid.RowDefinitions.Add(new() { Height = GridLength.Auto });
        }
        for (int index = 0; index < grid.Children.Count; index++)
        {
            var field = (FrameworkElement)grid.Children[index];
            Grid.SetRow(field, stacked ? index : index < 3 ? 0 : 1);
            Grid.SetColumn(field, stacked ? 0 : index < 3 ? index : index == 3 ? 0 : 2);
            Grid.SetColumnSpan(field, stacked ? 3 : index == 3 ? 2 : 1);
        }
    }

    private async void SaveSearchSettings_Click(object sender, RoutedEventArgs e) => await SaveSearchSettingsAsync();

    private async Task SaveSearchSettingsAsync()
    {
        using var trace = OperationDiagnostics.Begin(Guid.NewGuid().ToString("N"));
        if (_library is not { } library) { SearchSettingsFeedback.Text = UiText.Get("Select an available library first."); return; }
        if (_savingSearchSettings) return;
        _savingSearchSettings = true;
        SettingsPanel.IsEnabled = false;
        byte[]? embeddingKey = null, rerankerKey = null;
        bool entered = false;
        try
        {
            SearchModel? Model(string url, string name, int apiType) => string.IsNullOrWhiteSpace(url) && string.IsNullOrWhiteSpace(name) ? null : new(url.Trim().TrimEnd('/'), name.Trim(), ApiType: apiType == 1 ? "dashscope" : "openai");
            var embedding = Model(EmbeddingBaseUrl.Text, EmbeddingModelName.Text, EmbeddingApiType.SelectedIndex);
            var reranker = Model(RerankerBaseUrl.Text, RerankerModelName.Text, RerankerApiType.SelectedIndex);
            if (embedding is not null) ConfigurationValidation.ValidateSearchModel(embedding);
            if (reranker is not null) ConfigurationValidation.ValidateSearchModel(reranker);
            if (EmbeddingKey.Password.Length > 0) embeddingKey = Encoding.UTF8.GetBytes(EmbeddingKey.Password);
            if (RerankerKey.Password.Length > 0) rerankerKey = Encoding.UTF8.GetBytes(RerankerKey.Password);
            await _settingsGate.WaitAsync(_lifetime.Token); entered = true;
            if (!ReferenceEquals(library, _library)) throw new OperationCanceledException();
            CancelSemanticSearch();
            var saved = await new SearchCredentialService(library, new WindowsCredentialVault()).SaveAsync(embedding, reranker,
                embeddingKey is null ? null : embeddingKey.AsMemory(), rerankerKey is null ? null : rerankerKey.AsMemory(),
                RemoveEmbeddingKey.IsChecked == true, RemoveRerankerKey.IsChecked == true, _lifetime.Token);
            _settings = saved.Settings;
            _semanticBankResults.Clear();
            PopulateSearchSettings();
            OperationDiagnostics.Record("SearchSettings.Saved", new() { ["credential_cleanup_required"] = saved.CredentialCleanupRequired });
            SearchSettingsFeedback.Text = UiText.Get(saved.CredentialCleanupRequired ? "Saved, but the old key could not be removed. Save again later." : "Saved.");
        }
        catch (OperationCanceledException) { OperationDiagnostics.Record("SearchSettings.Cancelled", new()); }
        catch (Exception error) { AppLog.Error("SearchSettings.Save." + OperationDiagnostics.CorrelationId, error); SearchSettingsFeedback.Text = UiText.Get("Could not save search settings. Enter both Base URL and model name, and check the address and key."); }
        finally
        {
            if (embeddingKey is not null) CryptographicOperations.ZeroMemory(embeddingKey);
            if (rerankerKey is not null) CryptographicOperations.ZeroMemory(rerankerKey);
            if (entered) _settingsGate.Release();
            _savingSearchSettings = false;
            SettingsPanel.IsEnabled = true;
        }
    }

    private async void ClearSearchIndex_Click(object sender, RoutedEventArgs e)
    {
        using var trace = OperationDiagnostics.Begin(Guid.NewGuid().ToString("N"));
        if (_library is not { } library) return;
        CancelSemanticSearch();
        try
        {
            await library.ClearSearchVectorsAsync(_lifetime.Token);
            _semanticBankResults.Clear();
            OperationDiagnostics.Record("SearchIndex.Cleared", new());
            SearchSettingsFeedback.Text = UiText.Get("Local index cleared. The next meaning search will rebuild it after confirmation.");
        }
        catch (Exception error) { AppLog.Error("SearchIndex.Clear." + OperationDiagnostics.CorrelationId, error); SearchSettingsFeedback.Text = UiText.Get("Could not clear the search index. Try again."); }
    }
}
