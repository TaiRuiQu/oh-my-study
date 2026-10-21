using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.System;
using Windows.UI.ViewManagement;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private readonly UISettings _uiSettings = new();
    private NavigationViewItem? _localizedSettingsItem;
    private long _settingsContentCallback;

    private void LocalizeNavigationSettings()
    {
        if (Navigation.SettingsItem is not { } settings) return;
        var item = WinRT.CastExtensions.As<NavigationViewItem>(settings);
        if (!ReferenceEquals(item, _localizedSettingsItem))
        {
            _localizedSettingsItem?.UnregisterPropertyChangedCallback(ContentControl.ContentProperty, _settingsContentCallback);
            _localizedSettingsItem = item;
            _settingsContentCallback = item.RegisterPropertyChangedCallback(ContentControl.ContentProperty, static (sender, _) =>
            {
                var entry = (NavigationViewItem)sender;
                if (entry.Content is not string text || text != "设置") entry.Content = "设置";
            });
        }
        item.Content = "设置";
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(item, "设置");
    }

    private void InitializeWorkspaceAccessibility()
    {
        // Global accelerators must not create an implicit tooltip over the entire window.
        RootGrid.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        ToolTipService.SetToolTip(ImportPaperButton, "导入试卷 (Ctrl + O)");
        RootGrid.SizeChanged += (_, _) => ApplyWorkspaceLayout();
        AddAccelerator(VirtualKey.O, VirtualKeyModifiers.Control, () => OpenFiles_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.S, VirtualKeyModifiers.Control, async () =>
        {
            if (SettingsPanel.Visibility == Visibility.Visible) await SaveSettingsFormsAsync();
            else ViewModel.FinishEditCommand.Execute(null);
        });
        AddAccelerator(VirtualKey.E, VirtualKeyModifiers.Control, () => EditQuestion_Click(this, new RoutedEventArgs()));
        AddAccelerator(VirtualKey.Number0, VirtualKeyModifiers.Control, FitPage);
        AddAccelerator(VirtualKey.Number1, VirtualKeyModifiers.Control, () => SelectSection(0));
        AddAccelerator(VirtualKey.Number2, VirtualKeyModifiers.Control, () => SelectSection(1));
        AddAccelerator(VirtualKey.Number3, VirtualKeyModifiers.Control, () => SelectSection(2));
        AddAccelerator(VirtualKey.Number4, VirtualKeyModifiers.Control, () => SelectSection(3));
    }

    private void AddAccelerator(VirtualKey key, VirtualKeyModifiers modifiers, Action action)
    {
        var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
        accelerator.Invoked += (_, args) =>
        {
            // Native dialogs, input composition and their accelerators keep ownership of focus.
            if (_closed || RootGrid.XamlRoot is null || VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Any(p => p.Child is ContentDialog)) return;
            args.Handled = true; action();
        };
        RootGrid.KeyboardAccelerators.Add(accelerator);
    }

    private void SelectSection(int index)
    {
        Navigation.SelectedItem = Navigation.MenuItems[index];
        if (Navigation.MenuItems[index] is NavigationViewItem item) item.Focus(FocusState.Keyboard);
    }

    private bool? _questionListStacked;

    private void ApplyWorkspaceLayout()
    {
        bool compact = RootGrid.ActualWidth < 1050;
        bool source = SourcePane.Visibility == Visibility.Visible;
        WorkspaceGrid.ColumnDefinitions[0].Width = source && !compact ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        WorkspaceColumnsRight.Width = new GridLength(1, GridUnitType.Star);
        WorkspaceQuestionsRow.Height = new GridLength(0);
        QuestionPane.Visibility = source && compact ? Visibility.Collapsed : Visibility.Visible;
        Grid.SetColumn(SourcePane, 0); Grid.SetColumnSpan(SourcePane, compact ? 2 : 1);
        Grid.SetColumn(QuestionPane, source && !compact ? 1 : 0);
        Grid.SetColumnSpan(QuestionPane, source && !compact ? 1 : 2);
        Grid.SetRow(QuestionPane, 0);
        WorkspaceGrid.ColumnSpacing = source && !compact ? 16 : 0;
        WorkspaceGrid.RowSpacing = 0;
        bool stacked = source || RootGrid.ActualWidth < 800;
        bool listLayoutChanged = _questionListStacked != stacked;
        _questionListStacked = stacked;
        QuestionListColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(320);
        QuestionLayout.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        QuestionListRow.Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        QuestionList.MaxHeight = stacked ? 200 : double.PositiveInfinity;
        if (listLayoutChanged)
            DispatcherQueue.TryEnqueue(() =>
            {
                if (!_closed && QuestionList.SelectedItem is { } selected && QuestionList.Items.Contains(selected))
                    QuestionList.ScrollIntoView(selected);
            });
        QuestionLayout.RowDefinitions[2].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(QuestionDetail, stacked ? 0 : 1);
        Grid.SetRow(QuestionDetail, stacked ? 2 : 0);
        Grid.SetRowSpan(QuestionDetail, stacked ? 1 : 3);
        QuestionDetail.BorderThickness = stacked ? new Thickness(0, 1, 0, 0) : new Thickness(1, 0, 0, 0);
        bool narrowHeader = RootGrid.ActualWidth < 850;
        Grid.SetColumn(WorkspaceActions, narrowHeader ? 0 : 1);
        Grid.SetRow(WorkspaceActions, narrowHeader ? 1 : 0);
        Grid.SetColumnSpan(WorkspaceActions, narrowHeader ? 2 : 1);
        bool narrowToolbar = RootGrid.ActualWidth < 650;
        Grid.SetColumn(DocumentCommands, narrowToolbar ? 0 : 1);
        Grid.SetRow(DocumentCommands, narrowToolbar ? 1 : 0);
        Grid.SetColumnSpan(DocumentCommands, narrowToolbar ? 2 : 1);
        Grid.SetColumnSpan(BackToPapersButton, narrowToolbar ? 2 : 1);
        bool stackedCommands = RootGrid.ActualWidth < 880;
        DocumentCommands.Orientation = stackedCommands ? Orientation.Vertical : Orientation.Horizontal;
        DocumentCommandDivider.Visibility = stackedCommands ? Visibility.Collapsed : Visibility.Visible;
        bool narrowPapers = RootGrid.ActualWidth < 700;
        Grid.SetColumnSpan(PaperSearch, narrowPapers ? 3 : 1);
        Grid.SetRow(PaperSubjectFilter, narrowPapers ? 1 : 0); Grid.SetColumn(PaperSubjectFilter, narrowPapers ? 0 : 1);
        Grid.SetRow(PaperSort, narrowPapers ? 1 : 0); Grid.SetColumn(PaperSort, narrowPapers ? 1 : 2);
        Grid.SetColumnSpan(PaperSort, narrowPapers ? 2 : 1);
        PaperFilters.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        PaperFilters.ColumnDefinitions[1].Width = narrowPapers ? new GridLength(1, GridUnitType.Star) : new GridLength(160);
        PaperFilters.ColumnDefinitions[2].Width = narrowPapers ? new GridLength(0) : new GridLength(160);
        GradeButton.Visibility = DocumentToolbar.Visibility == Visibility.Visible && ViewModel.CurrentDocument is not null ? Visibility.Visible : Visibility.Collapsed;
        TaskActivity.Visibility = ViewModel.IsTaskRunning ? Visibility.Visible : Visibility.Collapsed;
        ApplyTaskLayout();
        var paneMode = compact ? NavigationViewPaneDisplayMode.LeftMinimal : NavigationViewPaneDisplayMode.Left;
        if (Navigation.PaneDisplayMode != paneMode)
        {
            Navigation.PaneDisplayMode = paneMode;
            Navigation.IsPaneOpen = !compact;
        }
        LocalizeNavigationSettings();
    }

    private void UpdateQuestionState()
    {
        bool any = ViewModel.Questions.Count > 0;
        EmptyQuestionState.Visibility = any ? Visibility.Collapsed : Visibility.Visible;
        QuestionListHeading.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        QuestionList.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        QuestionDetail.Visibility = any ? Visibility.Visible : Visibility.Collapsed;
        EmptyQuestionTitle.Text = ViewModel.SectionTitle == "错题" ? "还没有错题" : ViewModel.IsTaskRunning ? "正在识别题目" : "还没有题目";
        EmptyQuestionMessage.Text = ViewModel.SectionTitle == "错题" ? "批改后需要复习的题目会出现在这里。"
            : ViewModel.IsTaskRunning ? "题目识别后会自动出现在这里，可在任务中查看进度。"
            : ViewModel.CurrentDocument is not null ? "这份试卷还没有识别出题目，可在上方重新识别。"
            : "在试卷页面导入 PDF 或图片，识别后题目会自动加入题库。";
        GradeButton.Visibility = DocumentToolbar.Visibility == Visibility.Visible && ViewModel.CurrentDocument is not null ? Visibility.Visible : Visibility.Collapsed;
        TaskActivity.Visibility = ViewModel.IsTaskRunning && TasksPanel.Visibility != Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void ViewOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } document || document.Pages.Count == 0) return;
        SourcePane.Visibility = Visibility.Visible;
        ApplyWorkspaceLayout();
        await ShowSourcePageAsync(_sourcePageIndex);
        FitPage();
    }

    private void CloseOriginal_Click(object sender, RoutedEventArgs e)
    {
        SourcePane.Visibility = Visibility.Collapsed;
        ApplyWorkspaceLayout();
    }
}
