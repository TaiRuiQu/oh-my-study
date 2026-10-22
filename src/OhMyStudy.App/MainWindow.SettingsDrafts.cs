using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private bool _settingsFormsReady;
    private bool _modelSelectionSuppressed;
    private bool _settingsSelectionReview;
    private string _providerFormBaseline = "";
    private string _modelFormBaseline = "";
    private string ProviderFormState() => string.Join('\u001f', ProviderName.Text, ProviderUrl.Text, ProtocolPicker.SelectedIndex.ToString(CultureInfo.InvariantCulture));
    private string ModelFormState() => string.Join('\u001f', ModelId.Text, ModelDisplayName.Text, ModelContextWindow.Text,
        ModelVision.IsChecked?.ToString() ?? "", ModelTools.IsChecked?.ToString() ?? "", ModelPriceInput.Text, ModelPriceOutput.Text,
        ModelPriceCacheRead.Text, ModelPriceCacheWrite.Text,
        SelectedCurrency, ModelThinkingBudget.Text,
        string.Join(',', ModelThinkingOptions.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => c.Content)));
    private bool HasModelDraft => _settingsFormsReady && ModelEditor.Visibility == Visibility.Visible && ModelFormState() != _modelFormBaseline;
    private bool HasProviderDraft => _settingsFormsReady && (ProviderFormState() != _providerFormBaseline || ProviderKey.Password.Length > 0);
    private bool HasUnsavedSettingsChanges => HasModelDraft || HasProviderDraft;

    private async Task SaveSettingsFormsAsync()
    {
        if (HasModelDraft)
        {
            await SaveModelAsync();
            if (HasModelDraft) return;
        }
        if (HasProviderDraft) await SaveProviderAsync();
    }

    private async Task<bool> ConfirmDiscardSettingsAsync(bool modelOnly = false)
    {
        if (!(modelOnly ? HasModelDraft : HasUnsavedSettingsChanges)) return true;
        if (_settingsSelectionReview) return false;
        _settingsSelectionReview = true;
        try
        {
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "有未保存的设置",
                Content = modelOnly ? "继续后，未保存的模型修改会丢失。" : "继续后，未保存的供应商和模型修改会丢失。",
                PrimaryButtonText = "放弃修改",
                CloseButtonText = "返回",
                DefaultButton = ContentDialogButton.Close
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }
        finally { _settingsSelectionReview = false; }
    }

    private void DiscardSettingsForm()
    {
        if (_providers.FirstOrDefault(p => p.Id == _selectedProviderId) is { } provider) PopulateProviderEditor(provider);
        else ClearProviderEditor();
    }

    private void UpdateProviderEntry(Provider provider)
    {
        _refreshingProviders = true;
        try
        {
            int index = _providers.ToList().FindIndex(p => p.Id == provider.Id);
            if (index < 0) _providers.Add(provider); else _providers[index] = provider;
            ProviderPicker.SelectedItem = provider; _selectedProviderId = provider.Id;
        }
        finally { _refreshingProviders = false; }
        UpdateSettingsActionAvailability();
    }

    private void UpdateModelList(Provider provider)
    {
        ModelPicker.Visibility = provider.Models.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        _modelSelectionSuppressed = true;
        try
        {
            ModelPicker.ItemsSource = provider.Models;
            ModelPicker.SelectedItem = provider.Models.FirstOrDefault(m => m.Id == _selectedModelId) ?? provider.Models.FirstOrDefault();
        }
        finally { _modelSelectionSuppressed = false; }
        if (ModelPicker.SelectedItem is ModelConfig selected) PopulateModelEditor(selected);
        else { ModelEditor.Visibility = Visibility.Collapsed; _modelFormBaseline = ModelFormState(); }
        UpdateSettingsActionAvailability();
    }
}
