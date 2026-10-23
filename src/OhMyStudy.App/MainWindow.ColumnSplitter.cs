using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private double _questionListWidth = 320;
    private double _questionListHeight = 180;

    private void InitializePaneSplitters()
    {
        NavigationSplitter.ResizeRequested += (_, delta) =>
        {
            Navigation.OpenPaneLength = Math.Clamp(Navigation.OpenPaneLength + delta, 160, Math.Max(160, Math.Min(400, RootGrid.ActualWidth - 400)));
            UpdateNavigationSplitter();
        };
        Navigation.DisplayModeChanged += (_, _) => UpdateNavigationSplitter();
        Navigation.RegisterPropertyChangedCallback(NavigationView.IsPaneOpenProperty, (_, _) => UpdateNavigationSplitter());
        RootGrid.SizeChanged += (_, _) => UpdateNavigationSplitter();
    }

    private void UpdateNavigationSplitter()
    {
        NavigationSplitter.Visibility = Navigation.DisplayMode == NavigationViewDisplayMode.Expanded && Navigation.IsPaneOpen ? Visibility.Visible : Visibility.Collapsed;
        NavigationSplitter.Margin = new Thickness(Navigation.OpenPaneLength - 6, 0, 0, 0);
    }

    private void ApplyQuestionColumns()
    {
        if (QuestionColumnSplitter is null) return;
        bool stacked = _questionListStacked != false;
        double maximum = Math.Max(220, QuestionLayout.ActualWidth - 292);
        QuestionListColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(Math.Clamp(_questionListWidth, 220, maximum));
        QuestionLayout.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 12);
        QuestionLayout.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        QuestionColumnSplitter.Visibility = ViewModel.Questions.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        QuestionColumnSplitter.Orientation = stacked ? Orientation.Horizontal : Orientation.Vertical;
        Grid.SetColumn(QuestionColumnSplitter, stacked ? 0 : 1);
        Grid.SetRow(QuestionColumnSplitter, stacked ? 2 : 0);
        Grid.SetColumnSpan(QuestionColumnSplitter, stacked ? 3 : 1);
        Grid.SetRowSpan(QuestionColumnSplitter, stacked ? 1 : 4);
    }

    private void QuestionLayout_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyQuestionColumns();

    private void ResizeQuestionColumn(double width)
    {
        if (_questionListStacked != false) return;
        _questionListWidth = Math.Clamp(width, 220, Math.Max(220, QuestionLayout.ActualWidth - 292));
        ApplyQuestionColumns();
    }

    private void ResizeQuestionSplit(double change)
    {
        if (_questionListStacked == false) { ResizeQuestionColumn(QuestionListColumn.ActualWidth + change); return; }
        double maximum = Math.Max(60, QuestionLayout.ActualHeight - QuestionListHeading.ActualHeight - 264);
        _questionListHeight = Math.Clamp(QuestionListRow.ActualHeight + change, 60, maximum);
        QuestionListRow.Height = new GridLength(_questionListHeight);
    }
}
