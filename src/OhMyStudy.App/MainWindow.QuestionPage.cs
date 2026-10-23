using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.Services;
using OhMyStudy.App.ViewModels;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private int _openIndex = -1;
    private string _openContext = "";
    private long _openGeneration;
    private CancellationTokenSource? _openCancellation;
    private Task _openPresentationTask = Task.CompletedTask;
    private double _openPresentationWidth;
    private int _openLayoutRevision;

    private async Task OpenBankQuestionAsync(int index)
    {
        if (index < 0 || index >= _bankQuestions.Count || _library is not { } library) return;
        if (QuestionsPanel.Visibility == Visibility.Visible) RememberBankState();
        var summary = _bankQuestions[index];
        long generation = ++_openGeneration;
        _openIndex = index; _openContext = summary.Exam;
        ShowQuestionPanels(detail: true);
        QuestionBackLabel.Text = _bankSection == "mistakes" ? UiText.Get("Mistakes") : UiText.Get("Question bank");
        ViewModel.DocumentTitle = summary.Title;
        UpdateQuestionPager();
        QuestionPageHost.Content = new TextBlock { Text = UiText.Get("Loading question…") };
        try
        {
            var saved = await library.GetQuestionAsync(summary.Id, _lifetime.Token);
            if (_closed || generation != _openGeneration) return;
            ViewModel.Open(saved);
            QuestionPageScroll.ChangeView(null, 0, null, true);
            _openPresentationTask = PresentOpenQuestionAsync();
            await _openPresentationTask;
        }
        catch (OperationCanceledException) { }
        catch (KeyNotFoundException)
        {
            if (_closed || generation != _openGeneration) return;
            ViewModel.StatusMessage = UiText.Get("This question no longer exists.");
            await ReturnToBankAsync(reload: true);
        }
        catch (Exception error)
        {
            AppLog.Error("QuestionPage.Open", error);
            if (!_closed && generation == _openGeneration) QuestionPageHost.Content = new TextBlock { Text = UiText.Get("Question content is temporarily unavailable."), TextWrapping = TextWrapping.Wrap };
        }
    }

    private async Task PresentOpenQuestionAsync()
    {
        if (ViewModel.OpenQuestion.Saved is not { } saved || _library is not { } library) return;
        CancelOpenPresentation();
        _openCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellation = _openCancellation.Token;
        long generation = _openGeneration;
        QuestionPageHost.UpdateLayout();
        _openPresentationWidth = Math.Max(110, QuestionPageHost.ActualWidth);
        try
        {
            var content = await NativeQuestionPresenter.CreateAsync(saved.Question, library, cancellation, _openPresentationWidth, RootGrid.ActualTheme,
                _openContext);
            if (!_closed && generation == _openGeneration && ReferenceEquals(ViewModel.OpenQuestion.Saved, saved)) QuestionPageHost.Content = content;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppLog.Error("QuestionPage.Present", error);
            if (!_closed && generation == _openGeneration) QuestionPageHost.Content = new TextBlock { Text = UiText.Get("Question content is temporarily unavailable."), TextWrapping = TextWrapping.Wrap };
        }
    }

    private void CancelOpenPresentation()
    {
        var old = _openCancellation; _openCancellation = null;
        if (old is not null) { old.Cancel(); old.Dispose(); }
    }

    private void CancelOpenQuestion()
    {
        ++_openGeneration;
        CancelOpenPresentation();
        QuestionPageHost.Content = null;
    }

    private void UpdateQuestionPager()
    {
        QuestionPosition.Text = string.Format(UiText.Culture, "{0:N0} / {1:N0}", _openIndex + 1, _bankMatching);
        PreviousQuestionButton.IsEnabled = _openIndex > 0;
        NextQuestionButton.IsEnabled = _openIndex + 1 < _bankMatching;
    }

    private async void PreviousQuestion_Click(object sender, RoutedEventArgs e) => await OpenBankQuestionAsync(_openIndex - 1);

    private async void NextQuestion_Click(object sender, RoutedEventArgs e)
    {
        if (_openIndex + 1 >= _bankQuestions.Count) await LoadMoreBankAsync();
        await OpenBankQuestionAsync(_openIndex + 1);
    }

    private async void QuestionBack_Click(object sender, RoutedEventArgs e) => await ReturnToBankAsync(reload: false);

    private async Task ReturnToBankAsync(bool reload)
    {
        CancelOpenQuestion();
        ShowQuestionPanels(detail: false);
        ViewModel.DocumentTitle = ViewModel.SectionTitle;
        if (reload)
        {
            var state = _bankStates.GetValueOrDefault(_bankSection);
            await ReloadBankAsync(state?.Offset ?? 0, state?.Loaded ?? 0);
            return;
        }
        if (_openIndex < 0 || _openIndex >= _bankQuestions.Count) return;
        var item = _bankQuestions[_openIndex];
        BankList.UpdateLayout();
        BankList.ScrollIntoView(item);
        await Task.Yield();
        if (!_closed && BankList.ContainerFromItem(item) is ListViewItem container) container.Focus(FocusState.Programmatic);
    }

    private void QuestionPageScroll_SizeChanged(object sender, SizeChangedEventArgs e) =>
        QuestionPageBody.Width = Math.Max(0, e.NewSize.Width - QuestionPageScroll.Padding.Left - QuestionPageScroll.Padding.Right);

    private async void QuestionPageHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialized || _closed || QuestionReaderPage.Visibility != Visibility.Visible || Math.Abs(e.NewSize.Width - _openPresentationWidth) < 2) return;
        int revision = ++_openLayoutRevision;
        try
        {
            await Task.Delay(150, _lifetime.Token);
            if (revision != _openLayoutRevision || _closed || ViewModel.OpenQuestion.Saved is null) return;
            _openPresentationTask = PresentOpenQuestionAsync();
            await _openPresentationTask;
        }
        catch (OperationCanceledException) { }
    }

    private async void ViewInExam_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.OpenQuestion.Saved is not { } saved) return;
        CancelOpenQuestion();
        QuestionReaderPage.Visibility = Visibility.Collapsed;
        await LocateQuestionAsync(saved);
    }

    private async void EditOpenQuestion_Click(object sender, RoutedEventArgs e) => await EditQuestionAsync(ViewModel.OpenQuestion);

    private async void SaveOpenQuestion_Click(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync(ViewModel.OpenQuestion);

    private async void SaveSelectedNotes_Click(object sender, RoutedEventArgs e) => await ViewModel.SaveAsync(ViewModel.SelectedQuestion);

    private async Task RefreshOpenQuestionAsync(SavedQuestion saved)
    {
        if (QuestionReaderPage.Visibility != Visibility.Visible || ViewModel.OpenQuestion.Saved?.Id != saved.Id) return;
        if (_openIndex >= 0 && _openIndex < _bankQuestions.Count && _library is { } library)
        {
            var page = await library.QueryQuestionsAsync(BankQuery(_openIndex, 1), _lifetime.Token);
            if (page.Items.FirstOrDefault(item => item.Id == saved.Id) is { } summary) _bankQuestions[_openIndex] = new QuestionSummaryViewModel(summary);
        }
        _openPresentationTask = PresentOpenQuestionAsync();
        await _openPresentationTask;
    }
}
