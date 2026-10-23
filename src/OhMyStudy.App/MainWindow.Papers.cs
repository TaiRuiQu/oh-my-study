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
        var sample = new MenuFlyoutItem { Text = UiText.Get("View sample"), Icon = new FontIcon { Glyph = "\uE8A5" } };
        sample.Click += RestoreSample_Click;
        int index = menu.Items.IndexOf(PaperDetailSeparator);
        menu.Items.Insert(index < 0 ? menu.Items.Count : index, sample);
        _paperSampleAction = sample;
#endif
    }

    private void ShowPaperList()
    {
        PapersPanel.Visibility = Visibility.Visible; WorkspaceGrid.Visibility = Visibility.Collapsed; HideQuestionPanels();
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
        var delete = new MenuFlyoutItem { Text = UiText.Get("Delete exam"), Icon = new FontIcon { Glyph = "\uE74D" } };
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
            Title = UiText.Get("Delete exam?"),
            Content = UiText.Format("“{0}” and its questions and reference answers will be permanently deleted.", paper.Title),
            PrimaryButtonText = UiText.Get("Delete"),
            CloseButtonText = UiText.Get("Cancel"),
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
                ViewModel.StatusMessage = UiText.Get("Exam deleted.");
            }
            catch { if (!_closed) ViewModel.StatusMessage = UiText.Get("Exam deleted, but the list could not refresh."); }
        }
        catch (OperationCanceledException) { }
        catch (KeyNotFoundException)
        {
            if (open) ShowPaperList();
            await RefreshDocumentsAsync();
            ViewModel.StatusMessage = UiText.Get("This exam no longer exists.");
        }
        catch (InvalidOperationException error) { ViewModel.StatusMessage = error.Message; }
        catch { if (!_closed) ViewModel.StatusMessage = UiText.Get("Could not delete the exam. Try again later."); }
        finally { _deletingPaper = false; }
    }

    private void BackToPapers_Click(object sender, RoutedEventArgs e) { RememberViewport(); ++_documentGeneration; CancelPreviewLoad(); ShowPaperList(); }
    private void PaperFilters_Changed(object sender, TextChangedEventArgs e) { if (_initialized) ApplyPaperFilters(); }
    private void PaperFilter_SelectionChanged(object sender, SelectionChangedEventArgs e) { if (_initialized && !_updatingPaperFilters) ApplyPaperFilters(); }

    private void RefreshPaperSubjects()
    {
        string selected = PaperSubjectFilter.SelectedItem as string ?? UiText.Get("All subjects");
        _updatingPaperFilters = true;
        try
        {
            PaperSubjectFilter.Items.Clear(); PaperSubjectFilter.Items.Add(UiText.Get("All subjects"));
            foreach (string subject in _papers.Select(p => p.Subject).Distinct().Order(StringComparer.CurrentCulture)) PaperSubjectFilter.Items.Add(subject);
            PaperSubjectFilter.SelectedItem = PaperSubjectFilter.Items.Contains(selected) ? selected : UiText.Get("All subjects");
        }
        finally { _updatingPaperFilters = false; }
    }

    private void ApplyPaperFilters()
    {
        string query = PaperSearch.Text.Trim(), subject = PaperSubjectFilter.SelectedItem as string ?? UiText.Get("All subjects");
        IEnumerable<PaperViewModel> source = _papers.Where(p => (query.Length == 0 || p.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase) || p.Subject.Contains(query, StringComparison.CurrentCultureIgnoreCase)) && (subject == UiText.Get("All subjects") || p.Subject == subject));
        source = PaperSort.SelectedIndex switch
        {
            1 => source.OrderBy(p => p.Summary.Document.CreatedAt, StringComparer.Ordinal),
            2 => source.OrderBy(p => p.Title, StringComparer.CurrentCultureIgnoreCase),
            3 => source.OrderBy(p => p.Subject, StringComparer.CurrentCulture).ThenBy(p => p.Title),
            4 => source.OrderByDescending(p => p.Summary.Possible > 0 ? p.Summary.Earned / p.Summary.Possible : null),
            _ => source.OrderByDescending(p => p.Summary.Document.CreatedAt, StringComparer.Ordinal)
        };
        _filteredPapers.Clear(); foreach (var paper in source) _filteredPapers.Add(paper);
        PaperCount.Text = _filteredPapers.Count == _papers.Count ? UiText.Format("Exams: {0}", _papers.Count) : UiText.Format("Exams: {0} / {1}", _filteredPapers.Count, _papers.Count);
        PaperEmpty.Visibility = _filteredPapers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        PaperList.Visibility = _filteredPapers.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        PaperEmptyTitle.Text = _papers.Count == 0 ? UiText.Get("No exams yet") : UiText.Get("No exams found");
        PaperEmptyMessage.Text = _papers.Count == 0 ? UiText.Get("Import a PDF or exam image to get started.") : UiText.Get("Try another keyword or subject.");
    }

    private async void PaperList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not PaperViewModel paper) return;
        try { await LoadDocumentAsync(paper.Summary.Document); }
        catch { ViewModel.StatusMessage = UiText.Get("Could not open the exam. Try again later."); }
    }

    private async void EditPaperInfo_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } document || _library is null) return;
        var summary = _papers.FirstOrDefault(p => p.Id == document.Id)?.Summary;
        var title = new TextBox { Header = UiText.Get("Title"), Text = document.Name, MaxLength = 240 };
        var subject = new TextBox { Header = UiText.Get("Subject"), Text = summary?.Subject ?? "", PlaceholderText = UiText.Get("e.g. Mathematics, Chinese, English"), MaxLength = 60 };
        var earned = new TextBox { Header = UiText.Get("Score"), Text = summary?.Earned?.ToString(CultureInfo.InvariantCulture) ?? "", PlaceholderText = UiText.Get("Undetermined") };
        var possible = new TextBox { Header = UiText.Get("Maximum score"), Text = summary?.Possible?.ToString(CultureInfo.InvariantCulture) ?? "", PlaceholderText = UiText.Get("Undetermined") };
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 }; content.Children.Add(title); content.Children.Add(subject); content.Children.Add(earned); content.Children.Add(possible); content.Children.Add(feedback);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = UiText.Get("Exam details"), Content = content, PrimaryButtonText = UiText.Get("Save"), CloseButtonText = UiText.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true; var deferral = args.GetDeferral(); dialog.IsPrimaryButtonEnabled = false;
            try
            {
                static double? Number(string value) => string.IsNullOrWhiteSpace(value) ? null : double.Parse(value, CultureInfo.InvariantCulture);
                await _library.UpdatePaperAsync(document.Id, title.Text, subject.Text, Number(earned.Text), Number(possible.Text), _lifetime.Token);
                dialog.Hide();
                try { await RefreshDocumentsAsync(document.Id); } catch { ViewModel.StatusMessage = UiText.Get("Details saved. Reopen the exam to view them."); }
            }
            catch (Exception error) { feedback.Text = string.IsNullOrWhiteSpace(title.Text) ? UiText.Get("Enter an exam title.") : error is FormatException or ArgumentException ? UiText.Get("Enter valid scores. The score cannot exceed the maximum.") : UiText.Get("Could not save. Wait for recognition to finish and retry."); }
            finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }
}
