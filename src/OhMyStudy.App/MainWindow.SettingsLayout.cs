using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private void InitializeSettingsLayout()
    {
        foreach (var feedback in new[] { ProviderFeedback, ModelFeedback, ModelCatalogFeedback, ModelCnyPreview, ModelExtraInfo, LibraryFeedback, ExecutionDescription, OriginalCaption, LanguageFeedback, SearchSettingsFeedback })
        {
            feedback.RegisterPropertyChangedCallback(TextBlock.TextProperty, OptionalTextChanged);
            OptionalTextChanged(feedback, TextBlock.TextProperty);
        }
        _providers.CollectionChanged += (_, _) =>
            ProviderPicker.Visibility = _providers.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private static void OptionalTextChanged(DependencyObject sender, DependencyProperty property)
    {
        var text = (TextBlock)sender;
        text.Visibility = string.IsNullOrWhiteSpace(text.Text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void ProviderLayout_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool compact = e.NewSize.Width < 720;
        ProviderLayout.RowSpacing = 0;
        ProviderListColumn.Width = compact ? new GridLength(1, GridUnitType.Star) : new GridLength(Math.Clamp(ProviderSplitter.ColumnSize ?? 212, 180, Math.Max(180, e.NewSize.Width - 432)));
        ProviderLayout.ColumnDefinitions[1].Width = new GridLength(compact ? 0 : 12);
        ProviderLayout.ColumnDefinitions[2].Width = compact ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        ProviderLayout.RowDefinitions[0].Height = compact ? new GridLength(ProviderSplitter.RowSize ?? 200) : GridLength.Auto;
        ProviderLayout.RowDefinitions[1].Height = new GridLength(compact ? 12 : 0);
        ProviderLayout.RowDefinitions[2].Height = GridLength.Auto;
        ProviderSplitter.Visibility = Visibility.Visible;
        ProviderSplitter.Orientation = compact ? Orientation.Horizontal : Orientation.Vertical;
        ProviderSplitter.MinimumBefore = compact ? 140 : 180;
        ProviderSplitter.MinimumAfter = 420;
        Grid.SetColumn(ProviderSplitter, compact ? 0 : 1);
        Grid.SetRow(ProviderSplitter, compact ? 1 : 0);
        Grid.SetColumnSpan(ProviderSplitter, compact ? 3 : 1);
        Grid.SetRowSpan(ProviderSplitter, compact ? 1 : 3);
        ProviderSidebar.VerticalAlignment = compact ? VerticalAlignment.Stretch : VerticalAlignment.Top;
        Grid.SetColumnSpan(ProviderSidebar, compact ? 3 : 1);
        Grid.SetColumnSpan(ProviderDetails, compact ? 3 : 1);
        Grid.SetColumn(ProviderDetails, compact ? 0 : 2);
        Grid.SetRow(ProviderDetails, compact ? 2 : 0);

        double detailWidth = compact ? e.NewSize.Width : e.NewSize.Width - ProviderListColumn.Width.Value - 12;
        bool singleColumn = detailWidth < 540;
        PlaceSettingsField(ProviderName, 0, 0, singleColumn ? 2 : 1, 14);
        PlaceSettingsField(ProtocolPicker, singleColumn ? 1 : 0, singleColumn ? 0 : 1, singleColumn ? 2 : 1, 14);
        PlaceSettingsField(ProviderUrl, singleColumn ? 2 : 1, 0, singleColumn ? 2 : 1, 14);
        PlaceSettingsField(ProviderKey, singleColumn ? 3 : 1, singleColumn ? 0 : 1, singleColumn ? 2 : 1, 14);
        PlaceSettingsField(ModelDisplayName, 0, 0, singleColumn ? 2 : 1, 12);
        PlaceSettingsField(ModelId, singleColumn ? 1 : 0, singleColumn ? 0 : 1, singleColumn ? 2 : 1, 12);
        PlaceSettingsField(ModelContextWindow, 0, 0, singleColumn ? 2 : 1, 12);
        PlaceSettingsField(ModelMaxTokens, singleColumn ? 1 : 0, singleColumn ? 0 : 1, singleColumn ? 2 : 1, 12);
        bool stackPrices = detailWidth < 660;
        PlaceSettingsField(ModelPriceInput, 0, 0, stackPrices ? 5 : 1, 12);
        PlaceSettingsField(ModelPriceCacheRead, stackPrices ? 1 : 0, stackPrices ? 0 : 1, stackPrices ? 5 : 1, 12);
        PlaceSettingsField(ModelPriceCacheWrite, stackPrices ? 2 : 0, stackPrices ? 0 : 2, stackPrices ? 5 : 1, 12);
        PlaceSettingsField(ModelPriceOutput, stackPrices ? 3 : 0, stackPrices ? 0 : 3, stackPrices ? 5 : 1, 12);
        PlaceSettingsField(ModelCurrency, stackPrices ? 4 : 0, stackPrices ? 0 : 4, stackPrices ? 5 : 1, 12);
        ModelActionButtons.Orientation = detailWidth < (UiText.Language == "zh-CN" ? 320 : 540) ? Orientation.Vertical : Orientation.Horizontal;
        LibraryActionButtons.Orientation = e.NewSize.Width < (UiText.Language == "zh-CN" ? 380 : 660) ? Orientation.Vertical : Orientation.Horizontal;
        SearchModelActions.Orientation = e.NewSize.Width < (UiText.Language == "zh-CN" ? 380 : 520) ? Orientation.Vertical : Orientation.Horizontal;
    }

    private static void PlaceSettingsField(FrameworkElement field, int row, int column, int span, double spacing)
    {
        Grid.SetRow(field, row); Grid.SetColumn(field, column); Grid.SetColumnSpan(field, span);
        field.Margin = new Thickness(0, row > 0 ? spacing : 0, 0, 0);
    }

    private void UpdateSettingsActionAvailability()
    {
        bool hasProvider = ProviderPicker.SelectedItem is Provider;
        DeleteProviderButton.IsEnabled = hasProvider;
        FetchModelsButton.IsEnabled = hasProvider;
        AddModelButton.IsEnabled = hasProvider;
        TestModelButton.IsEnabled = hasProvider && ModelPicker.SelectedItem is ModelConfig;
        DeleteModelButton.IsEnabled = hasProvider && _selectedModelId is not null;
    }
}
