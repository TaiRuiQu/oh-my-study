using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.ViewModels;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private readonly List<PaperViewModel> _papers = [];
    private readonly ObservableCollection<PaperViewModel> _filteredPapers = [];
    private bool _updatingPaperFilters;
    private bool _deletingPaper;
#if DEBUG
    private MenuFlyoutItem? _paperSampleAction;
    private static readonly bool HasSampleAction = true;
#else
    private static readonly bool HasSampleAction = false;
#endif

    private void InitializePaperMenu()
    {
#if DEBUG
        if (PaperMoreButton.Flyout is not MenuFlyout menu) return;
        var sample = new MenuFlyoutItem { Text = UiText.Get("查看示例"), Icon = new FontIcon { Glyph = "\uE8A5" } };
        sample.Click += RestoreSample_Click;
        int index = menu.Items.IndexOf(PaperDetailSeparator);
        menu.Items.Insert(index < 0 ? menu.Items.Count : index, sample);
        _paperSampleAction = sample;
#endif
    }

    private void ShowPaperList()
    {
        PapersPanel.Visibility = Visibility.Visible; WorkspaceGrid.Visibility = Visibility.Collapsed;
        DocumentToolbar.Visibility = Visibility.Collapsed; SourcePane.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed; TasksPanel.Visibility = Visibility.Collapsed;
        WorkspaceActions.Visibility = Visibility.Visible; GradeButton.Visibility = Visibility.Collapsed;
        SetPaperMenuDetail(false); ApplyPaperHeaderActions(false);
        SectionCaption.Visibility = Visibility.Collapsed; WorkspaceNotice.Visibility = Visibility.Collapsed;
        WorkspaceStatus.Visibility = Visibility.Visible;
        ViewModel.ShowSection("papers"); ApplyPaperFilters(); ApplyWorkspaceLayout();
    }

    private void SetPaperMenuDetail(bool detail)
    {
        foreach (var action in new FrameworkElement[] { PaperPagesAction, PaperReferencesAction, PaperDeleteAction })
            action.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        PaperDetailSeparator.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        PaperMoreButton.Visibility = detail || HasSampleAction ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyPaperHeaderActions(bool detail)
    {
        if (!Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("AccentButtonStyle", out var value) || value is not Style accent) return;
        ImportPaperButton.Style = detail ? null : accent;
        GradeButton.Style = detail ? accent : null;
    }

    private void PaperRow_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { DataContext: PaperViewModel paper } element) return;
        var flyout = CreatePaperContextMenu(paper);
        if (args.TryGetPosition(element, out var position))
            flyout.ShowAt(element, new FlyoutShowOptions { Position = position });
        else
            flyout.ShowAt(element);
        args.Handled = true;
    }

    private MenuFlyout CreatePaperContextMenu(PaperViewModel paper)
    {
        var delete = new MenuFlyoutItem { Text = UiText.Get("删除试卷"), Icon = new FontIcon { Glyph = "\uE74D" } };
        if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("TaskErrorBrush", out var value) && value is Brush brush)
        {
            delete.Foreground = brush;
            if (delete.Icon is FontIcon icon) icon.Foreground = brush;
        }
        delete.Click += async (_, _) => await DeletePaperAsync(paper);
        var flyout = new MenuFlyout();
        flyout.Items.Add(delete);
        return flyout;
    }

    private async void DeletePaper_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } document) return;
        if (_papers.FirstOrDefault(paper => paper.Id == document.Id) is { } current) await DeletePaperAsync(current);
    }

    private async Task DeletePaperAsync(PaperViewModel paper)
    {
        if (_deletingPaper || _closed || _library is null) return;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            RequestedTheme = RootGrid.ActualTheme,
            Title = UiText.Get("删除试卷？"),
            Content = UiText.Format("“{0}”以及其中的题目、参考答案会一并删除，无法恢复。", paper.Title),
            PrimaryButtonText = UiText.Get("删除"),
            CloseButtonText = UiText.Get("取消"),
            DefaultButton = ContentDialogButton.Close
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _deletingPaper = true;
        bool open = ViewModel.CurrentDocument?.Id == paper.Id;
        try
        {
            await _library.DeletePaperAsync(paper.Id, _lifetime.Token);
            _viewports.Remove(paper.Id);
            _workspaceContexts.Remove("materials:" + paper.Id);
            if (open)
            {
                ++_documentGeneration; CancelPreviewLoad(); ReleaseReadingResources();
                ShowPaperList();
            }
            try
            {
                await RefreshDocumentsAsync();
                ViewModel.StatusMessage = UiText.Get("已删除试卷。");
            }
            catch { if (!_closed) ViewModel.StatusMessage = UiText.Get("已删除试卷，列表暂时无法更新。"); }
        }
        catch (OperationCanceledException) { }
        catch (KeyNotFoundException)
        {
            if (open) ShowPaperList();
            await RefreshDocumentsAsync();
            ViewModel.StatusMessage = UiText.Get("试卷已不存在。");
        }
        catch (InvalidOperationException error) { ViewModel.StatusMessage = error.Message; }
        catch { if (!_closed) ViewModel.StatusMessage = UiText.Get("未能删除试卷，请稍后重试。"); }
        finally { _deletingPaper = false; }
    }

    private void BackToPapers_Click(object sender, RoutedEventArgs e) { RememberViewport(); ++_documentGeneration; CancelPreviewLoad(); ShowPaperList(); }
    private void PaperFilters_Changed(object sender, TextChangedEventArgs e) { if (_initialized) ApplyPaperFilters(); }
    private void PaperFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_initialized && !_updatingPaperFilters) ApplyPaperFilters(); }

    private void RefreshPaperSubjects()
    {
        string selected = PaperSubjectFilter.SelectedItem as string ?? UiText.Get("全部学科");
        _updatingPaperFilters = true;
        try
        {
            PaperSubjectFilter.Items.Clear(); PaperSubjectFilter.Items.Add(UiText.Get("全部学科"));
            foreach (string subject in _papers.Select(p => p.Subject).Distinct().Order(StringComparer.CurrentCulture)) PaperSubjectFilter.Items.Add(subject);
            PaperSubjectFilter.SelectedItem = PaperSubjectFilter.Items.Contains(selected) ? selected : UiText.Get("全部学科");
        }
        finally { _updatingPaperFilters = false; }
    }

    private void ApplyPaperFilters()
    {
        string query = PaperSearch.Text.Trim(), subject = PaperSubjectFilter.SelectedItem as string ?? UiText.Get("全部学科");
        IEnumerable<PaperViewModel> source = _papers.Where(p => (query.Length == 0 || p.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || p.Subject.Contains(query, StringComparison.CurrentCultureIgnoreCase)) && (subject == UiText.Get("全部学科") || p.Subject == subject));
        source = PaperSort.SelectedIndex switch
        {
            1 => source.OrderBy(p => p.Summary.Document.CreatedAt, StringComparer.Ordinal),
            2 => source.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase),
            3 => source.OrderBy(p => p.Subject, StringComparer.CurrentCulture).ThenBy(p => p.Title),
            4 => source.OrderByDescending(p => p.Summary.Possible > 0 ? p.Summary.Earned / p.Summary.Possible : null),
            _ => source.OrderByDescending(p => p.Summary.Document.CreatedAt, StringComparer.Ordinal)
        };
        _filteredPapers.Clear(); foreach (var paper in source) _filteredPapers.Add(paper);
        PaperCount.Text = _filteredPapers.Count == _papers.Count ? UiText.Format("{0} 份试卷", _papers.Count) : UiText.Format("{0} / {1} 份试卷", _filteredPapers.Count, _papers.Count);
        PaperEmpty.Visibility = _filteredPapers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaperList.Visibility = _filteredPapers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PaperEmptyTitle.Text = _papers.Count == 0 ? UiText.Get("还没有试卷") : UiText.Get("没有找到试卷");
        PaperEmptyMessage.Text = _papers.Count == 0 ? UiText.Get("导入 PDF 或图片试卷，开始整理。") : UiText.Get("试试其他关键词或学科。");
    }

    private async void PaperList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PaperViewModel paper) return;
        try { await LoadDocumentAsync(paper.Summary.Document); }
        catch { ViewModel.StatusMessage = UiText.Get("试卷无法打开，请稍后重试。"); }
    }

    private async void EditPaperInfo_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } document || _library is null) return;
        var summary = _papers.FirstOrDefault(p => p.Id == document.Id)?.Summary;
        var title = new TextBox { Header = UiText.Get("标题"), Text = document.Name, MaxLength = 240 };
        var subject = new TextBox { Header = UiText.Get("学科"), Text = summary?.Subject ?? "", PlaceholderText = UiText.Get("如数学、语文、英语"), MaxLength = 60 };
        var earned = new TextBox { Header = UiText.Get("得分"), Text = summary?.Earned?.ToString(CultureInfo.InvariantCulture) ?? "", PlaceholderText = UiText.Get("未确定") };
        var possible = new TextBox { Header = UiText.Get("满分"), Text = summary?.Possible?.ToString(CultureInfo.InvariantCulture) ?? "", PlaceholderText = UiText.Get("未确定") };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 }; content.Children.Add(title); content.Children.Add(subject); content.Children.Add(earned); content.Children.Add(possible); content.Children.Add(feedback);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = UiText.Get("试卷信息"), Content = content, PrimaryButtonText = UiText.Get("保存"), CloseButtonText = UiText.Get("取消"), DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; var deferral = args.GetDeferral(); dialog.IsPrimaryButtonEnabled = false;
            try
            {
                static double? Number(string value) => string.IsNullOrWhiteSpace(value) ? null : double.Parse(value, CultureInfo.InvariantCulture);
                await _library.UpdatePaperAsync(document.Id, title.Text, subject.Text, Number(earned.Text), Number(possible.Text), _lifetime.Token);
                dialog.Hide();
                try { await RefreshDocumentsAsync(document.Id); } catch { ViewModel.StatusMessage = UiText.Get("信息已保存，请重新打开试卷查看。"); }
            }
            catch (Exception error) { feedback.Text = string.IsNullOrWhiteSpace(title.Text) ? UiText.Get("请输入试卷标题。") : error is FormatException or ArgumentException ? UiText.Get("请填写有效分数，得分不能超过满分。") : UiText.Get("未能保存，请等待识别结束后重试。"); }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
}
