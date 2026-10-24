using Microsoft.UI.Xaml;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.ViewModels;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task ConfirmQuestionAsync(QuestionViewModel selected)
    {
        if (_library is not { } library || selected.Saved is not { } saved) return;
        string notes = selected.Notes;
        try
        {
            var result = await library.ConfirmQuestionAsync(saved.Id, _lifetime.Token);
            selected.AcceptSaved(result);
            selected.Notes = notes;
            if (QuestionReaderPage.Visibility == Visibility.Visible) await RefreshOpenQuestionAsync(result);
            else
            {
                _questionPresentationTask = PresentSelectedQuestionAsync(clear: false);
                await _questionPresentationTask;
            }
            ViewModel.StatusMessage = UiText.Get("Result confirmed.");
        }
        catch (Exception error)
        {
            AppLog.Error("Question.Confirm", error);
            ViewModel.StatusMessage = UiText.Get("Could not confirm. Wait for the task to finish and retry.");
        }
    }
}
