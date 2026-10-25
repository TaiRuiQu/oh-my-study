using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async void UploadStudentAnswers_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } document) return;
        try { await PickAndImportAsync(documentId: document.Id, studentAnswers: true); }
        catch (Exception error)
        {
            AppLog.Error("StudentAnswers.PickFiles", error);
            ViewModel.StatusMessage = UiText.Get("Could not open the file. Try again.");
        }
    }

    private async void StudentAnswer_Click(object sender, RoutedEventArgs e)
    {
        var selected = QuestionReaderPage.Visibility == Visibility.Visible ? ViewModel.OpenQuestion : ViewModel.SelectedQuestion;
        if (_questionEditorOpen || _library is not { } library || selected.Saved is not { } saved) return;
        _questionEditorOpen = true;
        try
        {
            var fields = new StackPanel { Spacing = 12, MinWidth = 320, MaxWidth = 560 };
            var entries = new Dictionary<string, TextBox>(StringComparer.Ordinal);
            foreach (var item in saved.Question.Items)
            {
                var text = new TextBox
                {
                    Header = string.IsNullOrWhiteSpace(item.Label) ? UiText.Get("Student answer") : UiText.Format("Subquestion {0}", item.Label),
                    Text = item.Attempt?.StudentAnswer ?? "",
                    AcceptsReturn = true,
                    TextWrapping = TextWrapping.Wrap,
                    MinHeight = 96
                };
                entries.Add(item.Key, text);
                fields.Children.Add(text);
            }
            var feedback = new TextBlock { Visibility = Visibility.Collapsed, TextWrapping = TextWrapping.Wrap };
            fields.Children.Add(feedback);
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = UiText.Get("Student answer"),
                Content = new ScrollViewer { Content = fields, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled },
                PrimaryButtonText = UiText.Get("Save"),
                CloseButtonText = UiText.Get("Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            SavedQuestion? result = null;
            dialog.PrimaryButtonClick += async (_, args) =>
            {
                var deferral = args.GetDeferral();
                dialog.IsPrimaryButtonEnabled = false;
                try { result = await library.SaveStudentAnswersAsync(saved.Id, entries.ToDictionary(pair => pair.Key, pair => pair.Value.Text), _lifetime.Token); }
                catch (Exception error)
                {
                    AppLog.Error("StudentAnswers.Save", error);
                    args.Cancel = true;
                    feedback.Text = UiText.Get("Not saved. Wait for the task to finish and retry.");
                    feedback.Visibility = Visibility.Visible;
                }
                finally { dialog.IsPrimaryButtonEnabled = true; deferral.Complete(); }
            };
            await dialog.ShowAsync();
            if (result is null) return;
            string notes = selected.Notes;
            selected.AcceptSaved(result);
            selected.Notes = notes;
            if (QuestionReaderPage.Visibility == Visibility.Visible) await RefreshOpenQuestionAsync(result);
            else await RefreshDocumentsAsync(saved.DocumentId);
            ViewModel.StatusMessage = UiText.Get("Student answers added. Recognize again to grade them.");
        }
        catch (Exception error)
        {
            AppLog.Error("StudentAnswers.Edit", error);
            ViewModel.StatusMessage = UiText.Get("Could not open this question. Select it again.");
        }
        finally { _questionEditorOpen = false; }
    }
}
