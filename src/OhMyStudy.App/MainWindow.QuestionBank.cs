using System.Collections.ObjectModel;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.ViewModels;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private const int BankPageSize = 100;
    private sealed record BankState(string Search, string? Subject, int Filter, int Sort, double Offset, int Loaded, string? QuestionId, bool Meaning);
    private readonly ObservableCollection<QuestionSummaryViewModel> _bankQuestions = [];
    private readonly Dictionary<string, BankState> _bankStates = new(StringComparer.Ordinal);
    private string _bankSection = "library";
    private int _bankMatching;
    private int _bankTotal;
    private bool _bankLoading;
    private bool _updatingBankFilters;
    private long _bankGeneration;
    private int _bankSearchRevision;
    private ScrollViewer? _bankScroll;
    private (bool Narrow, bool Mistakes)? _bankFilterLayout;

    private void ShowQuestionPanels(bool detail)
    {
        SettingsPanel.Visibility = TasksPanel.Visibility = PapersPanel.Visibility = WorkspaceGrid.Visibility = Visibility.Collapsed;
        DocumentToolbar.Visibility = SourcePane.Visibility = WorkspaceActions.Visibility = WorkspaceNotice.Visibility = SectionCaption.Visibility = Visibility.Collapsed;
        QuestionsPanel.Visibility = detail ? Visibility.Collapsed : Visibility.Visible;
        QuestionReaderPage.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceStatus.Visibility = Visibility.Visible;
    }

    private void HideQuestionPanels()
    {
        CancelSemanticSearch();
        if (QuestionsPanel.Visibility == Visibility.Visible) RememberBankState();
        QuestionsPanel.Visibility = QuestionReaderPage.Visibility = Visibility.Collapsed;
        CancelOpenQuestion();
    }

    private async Task ShowQuestionBankAsync(string section)
    {
        if (QuestionsPanel.Visibility == Visibility.Visible) RememberBankState();
        CancelOpenQuestion();
        _bankSection = section;
        ShowQuestionPanels(detail: false);
        ViewModel.ShowSection(section);
        var state = _bankStates.GetValueOrDefault(section);
        _updatingBankFilters = true;
        try
        {
            QuestionSearch.Text = state?.Search ?? "";
            QuestionSearchMode.SelectedIndex = state?.Meaning == true ? 1 : 0;
            QuestionResultFilter.SelectedIndex = state?.Filter ?? 0;
            QuestionSort.SelectedIndex = state?.Sort ?? 0;
            QuestionSubjectFilter.Tag = state?.Subject;
        }
        finally { _updatingBankFilters = false; }
        SetSemanticFeedback("");
        UpdateSearchMode();
        ApplyWorkspaceLayout();
        await ReloadBankAsync(state?.Offset ?? 0, state?.Loaded ?? 0, state?.QuestionId);
    }

    private void RememberBankState()
    {
        string? focused = _openIndex >= 0 && _openIndex < _bankQuestions.Count ? _bankQuestions[_openIndex].Id : null;
        _bankStates[_bankSection] = new(QuestionSearch.Text, SelectedBankSubject(), QuestionResultFilter.SelectedIndex, QuestionSort.SelectedIndex,
            _bankScroll?.VerticalOffset ?? 0, _bankQuestions.Count, focused, IsMeaningSearch);
    }

    private string? SelectedBankSubject() => QuestionSubjectFilter.SelectedIndex > 0 ? QuestionSubjectFilter.SelectedItem as string : QuestionSubjectFilter.Tag as string;

    private QuestionQuery BankQuery(int offset, int limit) => new(
        _bankSection == "mistakes" ? QuestionFilter.Mistakes : (QuestionFilter)Math.Max(0, QuestionResultFilter.SelectedIndex),
        (QuestionSort)Math.Max(0, QuestionSort.SelectedIndex),
        QuestionSearch.Text.Trim() is { Length: > 0 } search ? search : null,
        SelectedBankSubject(), offset, limit);

    private async Task ReloadBankAsync(double offset = 0, int loaded = 0, string? focusId = null, bool keepPosition = false)
    {
        long generation = ++_bankGeneration;
        _bankLoading = true;
        BankProgress.IsActive = true; BankProgress.Visibility = _bankQuestions.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        try
        {
            var query = BankQuery(0, Math.Clamp(loaded, BankPageSize, 500));
            QuestionPage page;
            List<string> subjects;
            if (_library is { } library)
            {
                page = IsMeaningSearch ? await SemanticBankPageAsync(library, query) : await library.QueryQuestionsAsync(query, _lifetime.Token);
                subjects = await library.ListQuestionSubjectsAsync(_lifetime.Token);
            }
            else (page, subjects) = (SampleBankPage(query), []);
            if (_closed || generation != _bankGeneration) return;
            RefreshBankSubjects(subjects);
            for (int index = 0; index < page.Items.Count; index++)
            {
                if (index < _bankQuestions.Count && _bankQuestions[index].Summary == page.Items[index]) continue;
                if (index < _bankQuestions.Count) _bankQuestions[index] = new QuestionSummaryViewModel(page.Items[index]);
                else _bankQuestions.Add(new QuestionSummaryViewModel(page.Items[index]));
            }
            while (_bankQuestions.Count > page.Items.Count) _bankQuestions.RemoveAt(_bankQuestions.Count - 1);
            _bankMatching = page.Matching; _bankTotal = page.Total;
            UpdateBankState();
            if (keepPosition) return;
            if (offset > 0 || focusId is not null)
            {
                BankList.UpdateLayout();
                if (focusId is not null && _bankQuestions.FirstOrDefault(item => item.Id == focusId) is { } focused) BankList.ScrollIntoView(focused);
                else _bankScroll?.ChangeView(null, offset, null, true);
            }
            else _bankScroll?.ChangeView(null, 0, null, true);
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppLog.Error("QuestionBank.Load", error);
            if (!_closed && generation == _bankGeneration) ViewModel.StatusMessage = UiText.Get("Could not load questions. Try again later.");
        }
        finally
        {
            if (generation == _bankGeneration) { _bankLoading = false; BankProgress.IsActive = false; BankProgress.Visibility = Visibility.Collapsed; }
        }
    }

    private async Task LoadMoreBankAsync()
    {
        if (IsMeaningSearch || _bankLoading || _bankQuestions.Count >= _bankMatching || _library is not { } library) return;
        long generation = _bankGeneration;
        _bankLoading = true;
        try
        {
            var page = await library.QueryQuestionsAsync(BankQuery(_bankQuestions.Count, BankPageSize), _lifetime.Token);
            if (_closed || generation != _bankGeneration) return;
            var known = _bankQuestions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
            foreach (var summary in page.Items.Where(summary => known.Add(summary.Id))) _bankQuestions.Add(new QuestionSummaryViewModel(summary));
            _bankMatching = page.Matching; _bankTotal = page.Total;
            UpdateBankState();
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("QuestionBank.LoadMore", error); }
        finally { if (generation == _bankGeneration) _bankLoading = false; }
    }

    private QuestionPage SampleBankPage(QuestionQuery query)
    {
        var all = ViewModel.SampleQuestions.Where(question => query.Filter != QuestionFilter.Mistakes || question.IsIncorrect)
            .Select(question => new QuestionSummary("sample-" + question.Number.ToString(CultureInfo.InvariantCulture), "sample", UiText.Get("Functions and derivatives · Practice"),
                question.Number.ToString(CultureInfo.InvariantCulture), "", "", question.PreviewText, 1, null, null,
                question.IsIncorrect ? Verdict.Partial : Verdict.Correct, question.IsIncorrect, false)).ToList();
        return new(all.Skip(query.Offset).Take(query.Limit).ToList(), all.Count, all.Count);
    }

    private void RefreshBankSubjects(IReadOnlyList<string> subjects)
    {
        string? selected = SelectedBankSubject();
        _updatingBankFilters = true;
        try
        {
            QuestionSubjectFilter.Items.Clear(); QuestionSubjectFilter.Items.Add(UiText.Get("All subjects"));
            foreach (string subject in subjects) QuestionSubjectFilter.Items.Add(subject);
            int index = selected is null ? 0 : QuestionSubjectFilter.Items.IndexOf(selected);
            QuestionSubjectFilter.SelectedIndex = Math.Max(0, index);
            QuestionSubjectFilter.Tag = null;
        }
        finally { _updatingBankFilters = false; }
    }

    private void UpdateBankState()
    {
        string matching = _bankMatching.ToString("N0", UiText.Culture), total = _bankTotal.ToString("N0", UiText.Culture);
        BankCount.Text = _bankMatching == _bankTotal ? UiText.Format("Questions: {0}", matching) : UiText.Format("Questions: {0} / {1}", matching, total);
        bool empty = _bankQuestions.Count == 0;
        BankEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        BankList.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (!empty && _bankScroll is null) { BankList.UpdateLayout(); AttachBankScroll(); }
        bool mistakes = _bankSection == "mistakes";
        var query = BankQuery(0, 1);
        if (IsMeaningSearch && (!_semanticBankResults.TryGetValue(_bankSection, out var result) || result.Request != SemanticRequest))
        {
            BankEmptyIcon.Glyph = "\uE721";
            BankEmptyTitle.Text = UiText.Get("Search by meaning");
            BankEmptyMessage.Text = UiText.Get("Describe what you want to find, then press Search or Enter. Model calls require confirmation.");
            return;
        }
        if (_bankTotal > 0 || query.Search is not null || query.Subject is not null || query.Filter != QuestionFilter.All && !mistakes)
        {
            BankEmptyIcon.Glyph = "\uE721";
            BankEmptyTitle.Text = UiText.Get("No questions found");
            BankEmptyMessage.Text = UiText.Get("Try another keyword, subject, or result filter.");
            return;
        }
        BankEmptyIcon.Glyph = mistakes ? "\uE734" : "\uE8F1";
        BankEmptyTitle.Text = mistakes ? UiText.Get("No mistakes yet") : ViewModel.IsTaskRunning ? UiText.Get("Recognizing questions") : UiText.Get("No questions yet");
        BankEmptyMessage.Text = mistakes ? UiText.Get("Questions to review after grading will appear here.")
            : ViewModel.IsTaskRunning ? UiText.Get("Recognized questions appear here automatically. Check progress in Tasks.")
            : UiText.Get("Import a PDF or image in Exams. Recognized questions join your question bank automatically.");
    }

    private void BankList_Loaded(object sender, RoutedEventArgs e) => AttachBankScroll();

    private void AttachBankScroll()
    {
        if (_bankScroll is not null || FindVisualChild<ScrollViewer>(BankList) is not { } scroll) return;
        _bankScroll = scroll;
        scroll.ViewChanged += async (_, _) =>
        {
            if (scroll.ScrollableHeight - scroll.VerticalOffset < 1200) await LoadMoreBankAsync();
        };
    }

    private async void QuestionSearch_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_initialized || _updatingBankFilters) return;
        if (IsMeaningSearch)
        {
            CancelSemanticSearch();
            SetSemanticFeedback("");
            await ReloadBankAsync();
            return;
        }
        int revision = ++_bankSearchRevision;
        try { await Task.Delay(250, _lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (revision == _bankSearchRevision && !_closed) await ReloadBankAsync();
    }

    private async void QuestionFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingBankFilters || QuestionsPanel.Visibility != Visibility.Visible) return;
        if (IsMeaningSearch)
        {
            CancelSemanticSearch();
            SetSemanticFeedback(UiText.Get("Filters changed. Press Search to refresh meaning results."));
        }
        await ReloadBankAsync();
    }

    private async void BankList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is QuestionSummaryViewModel item) await OpenBankQuestionAsync(_bankQuestions.IndexOf(item));
    }

    private void ApplyQuestionBankLayout()
    {
        UpdateSearchMode();
        bool narrowToolbar = RootGrid.ActualWidth < 760;
        Grid.SetRow(QuestionPager, narrowToolbar ? 1 : 0); Grid.SetColumn(QuestionPager, narrowToolbar ? 0 : 1); Grid.SetColumnSpan(QuestionPager, narrowToolbar ? 3 : 1);
        bool narrow = RootGrid.ActualWidth < 860, mistakes = _bankSection == "mistakes";
        if (_bankFilterLayout == (narrow, mistakes)) return;
        _bankFilterLayout = (narrow, mistakes);
        QuestionResultFilter.Visibility = mistakes ? Visibility.Collapsed : Visibility.Visible;
        FrameworkElement[] choices = mistakes ? [QuestionSubjectFilter, QuestionSort] : [QuestionSubjectFilter, QuestionResultFilter, QuestionSort];
        var columns = QuestionFilters.ColumnDefinitions;
        columns.Clear();
        if (!narrow) columns.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        foreach (var choice in choices) columns.Add(new ColumnDefinition { Width = narrow ? new GridLength(1, GridUnitType.Star) : new GridLength(ReferenceEquals(choice, QuestionSort) ? 170 : 160) });
        Grid.SetRow(QuestionSearchField, 0); Grid.SetColumn(QuestionSearchField, 0); Grid.SetColumnSpan(QuestionSearchField, narrow ? choices.Length : 1);
        for (int index = 0; index < choices.Length; index++)
        {
            Grid.SetRow(choices[index], narrow ? 1 : 0); Grid.SetColumn(choices[index], narrow ? index : index + 1); Grid.SetColumnSpan(choices[index], 1);
        }
    }
}
