using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.ViewModels;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private readonly List<PaperViewModel> _papers = [];
    private readonly ObservableCollection<PaperViewModel> _filteredPapers = [];
    private bool _updatingPaperFilters;

    private void ShowPaperList()
    {
        PapersPanel.Visibility = Visibility.Visible; WorkspaceGrid.Visibility = Visibility.Collapsed;
        DocumentToolbar.Visibility = Visibility.Collapsed; SourcePane.Visibility = Visibility.Collapsed;
        SettingsPanel.Visibility = Visibility.Collapsed; TasksPanel.Visibility = Visibility.Collapsed;
        WorkspaceActions.Visibility = Visibility.Visible; GradeButton.Visibility = Visibility.Collapsed;
        PaperMoreButton.Visibility = Visibility.Visible; SetPaperMenuDetail(false);
        SectionCaption.Visibility = Visibility.Collapsed; WorkspaceNotice.Visibility = Visibility.Collapsed;
        WorkspaceStatus.Visibility = Visibility.Visible;
        ViewModel.ShowSection("papers"); ApplyPaperFilters(); ApplyWorkspaceLayout();
    }

    private void SetPaperMenuDetail(bool detail)
    {
        foreach (var action in new[] { PaperAddReferenceAction, PaperPagesAction, PaperExportAction, PaperPrintAction, PaperReferencesAction }) action.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BackToPapers_Click(object sender, RoutedEventArgs e) { RememberViewport(); ++_documentGeneration; CancelPreviewLoad(); ShowPaperList(); }
    private void PaperFilters_Changed(object sender, TextChangedEventArgs e) { if (_initialized) ApplyPaperFilters(); }
    private void PaperFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_initialized && !_updatingPaperFilters) ApplyPaperFilters(); }

    private void RefreshPaperSubjects()
    {
        string selected = PaperSubjectFilter.SelectedItem as string ?? "全部学科";
        _updatingPaperFilters = true;
        try
        {
            PaperSubjectFilter.Items.Clear(); PaperSubjectFilter.Items.Add("全部学科");
            foreach (string subject in _papers.Select(p => p.Subject).Distinct().Order(StringComparer.CurrentCulture)) PaperSubjectFilter.Items.Add(subject);
            PaperSubjectFilter.SelectedItem = PaperSubjectFilter.Items.Contains(selected) ? selected : "全部学科";
        }
        finally { _updatingPaperFilters = false; }
    }

    private void ApplyPaperFilters()
    {
        string query = PaperSearch.Text.Trim(), subject = PaperSubjectFilter.SelectedItem as string ?? "全部学科";
        IEnumerable<PaperViewModel> source = _papers.Where(p => (query.Length == 0 || p.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || p.Subject.Contains(query, StringComparison.CurrentCultureIgnoreCase)) && (subject == "全部学科" || p.Subject == subject));
        source = PaperSort.SelectedIndex switch
        {
            1 => source.OrderBy(p => p.Summary.Document.CreatedAt, StringComparer.Ordinal),
            2 => source.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase),
            3 => source.OrderBy(p => p.Subject, StringComparer.CurrentCulture).ThenBy(p => p.Title),
            4 => source.OrderByDescending(p => p.Summary.Possible > 0 ? p.Summary.Earned / p.Summary.Possible : null),
            _ => source.OrderByDescending(p => p.Summary.Document.CreatedAt, StringComparer.Ordinal)
        };
        _filteredPapers.Clear(); foreach (var paper in source) _filteredPapers.Add(paper);
        PaperCount.Text = _filteredPapers.Count == _papers.Count ? $"{_papers.Count} 份试卷" : $"{_filteredPapers.Count} / {_papers.Count} 份试卷";
        PaperEmpty.Visibility = _filteredPapers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaperList.Visibility = _filteredPapers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PaperEmptyTitle.Text = _papers.Count == 0 ? "还没有试卷" : "没有找到试卷";
        PaperEmptyMessage.Text = _papers.Count == 0 ? "导入 PDF 或图片试卷，开始整理。" : "试试其他关键词或学科。";
    }

    private async void PaperList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PaperViewModel paper) return;
        try { await LoadDocumentAsync(paper.Summary.Document); }
        catch { ViewModel.StatusMessage = "试卷无法打开，请稍后重试。"; }
    }

    private async void EditPaperInfo_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } document || _library is null) return;
        var summary = _papers.FirstOrDefault(p => p.Id == document.Id)?.Summary;
        var title = new TextBox { Header = "标题", Text = document.Name, MaxLength = 240 };
        var subject = new TextBox { Header = "学科", Text = summary?.Subject ?? "", PlaceholderText = "如数学、语文、英语", MaxLength = 60 };
        var earned = new TextBox { Header = "得分", Text = summary?.Earned?.ToString(CultureInfo.InvariantCulture) ?? "", PlaceholderText = "未确定" };
        var possible = new TextBox { Header = "满分", Text = summary?.Possible?.ToString(CultureInfo.InvariantCulture) ?? "", PlaceholderText = "未确定" };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 }; content.Children.Add(title); content.Children.Add(subject); content.Children.Add(earned); content.Children.Add(possible); content.Children.Add(feedback);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = "试卷信息", Content = content, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; var deferral = args.GetDeferral(); dialog.IsPrimaryButtonEnabled = false;
            try
            {
                static double? Number(string value) => string.IsNullOrWhiteSpace(value) ? null : double.Parse(value, CultureInfo.InvariantCulture);
                await _library.UpdatePaperAsync(document.Id, title.Text, subject.Text, Number(earned.Text), Number(possible.Text), _lifetime.Token);
                dialog.Hide();
                try { await RefreshDocumentsAsync(document.Id); } catch { ViewModel.StatusMessage = "信息已保存，请重新打开试卷查看。"; }
            }
            catch (Exception error) { feedback.Text = string.IsNullOrWhiteSpace(title.Text) ? "请输入试卷标题。" : error is FormatException or ArgumentException ? "请填写有效分数，得分不能超过满分。" : "未能保存，请等待识别结束后重试。"; }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
}
