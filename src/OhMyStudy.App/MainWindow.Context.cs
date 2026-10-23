using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private sealed record WorkspaceContext(string? QuestionId, int SampleNumber, double ListY, double ContentY, string Focus, int SelectionStart, int SelectionLength);
    private readonly Dictionary<string, WorkspaceContext> _workspaceContexts = new(StringComparer.Ordinal);
    private string _workspaceContextKey = "materials:";
    private long _questionPresentationGeneration;
    private long _questionReloadGeneration;
    private long _documentListGeneration;
    private bool _suppressNavigation;

    private void ShowMaterialsNavigation()
    {
        _suppressNavigation = true;
        try { Navigation.SelectedItem = Navigation.MenuItems[0]; }
        finally { _suppressNavigation = false; }
        SettingsPanel.Visibility = Visibility.Collapsed; TasksPanel.Visibility = Visibility.Collapsed; PapersPanel.Visibility = Visibility.Collapsed;
        HideQuestionPanels();
        WorkspaceActions.Visibility = Visibility.Visible; WorkspaceNotice.Visibility = Visibility.Visible;
        SectionCaption.Visibility = Visibility.Visible;
        WorkspaceGrid.Visibility = Visibility.Visible; DocumentToolbar.Visibility = Visibility.Visible; DocumentCommands.Visibility = Visibility.Visible;
        WorkspaceStatus.Visibility = Visibility.Visible; SetPaperMenuDetail(true); ApplyPaperHeaderActions(true);
        ViewModel.ShowSection("materials");
        UpdateQuestionState();
    }

    private void RememberWorkspaceContext()
    {
        if (!_initialized || _closed || RootGrid.XamlRoot is null || WorkspaceGrid.Visibility != Visibility.Visible) return;
        var focused = FocusManager.GetFocusedElement(RootGrid.XamlRoot) as DependencyObject;
        string focus = IsWithin(focused, NotesEditor) ? "notes" : IsWithin(focused, QuestionList) ? "list"
            : IsWithin(focused, SourceViewer) ? "source" : IsWithin(focused, QuestionContentHost) ? "content" : "";
        _workspaceContexts[_workspaceContextKey] = new(ViewModel.SelectedQuestion.Saved?.Id, ViewModel.SelectedQuestion.Number,
            FindVisualChild<ScrollViewer>(QuestionList)?.VerticalOffset ?? 0, QuestionDetail.VerticalOffset,
            focus, NotesEditor.SelectionStart, NotesEditor.SelectionLength);
    }

    private void RestoreQuestionContentOffset()
    {
        if (_workspaceContexts.TryGetValue(_workspaceContextKey, out var context)
            && context.QuestionId == ViewModel.SelectedQuestion.Saved?.Id)
        {
            QuestionDetail.UpdateLayout();
            QuestionDetail.ChangeView(null, context.ContentY, null, true);
        }
    }

    private async Task RestoreWorkspaceContextAsync(string key, long generation)
    {
        _workspaceContextKey = key;
        if (!_workspaceContexts.TryGetValue(key, out var context)) return;
        var selected = ViewModel.Questions.FirstOrDefault(question => context.QuestionId is not null
            ? question.Saved?.Id == context.QuestionId : question.Number == context.SampleNumber);
        if (selected is not null) ViewModel.SelectedQuestion = selected;
        if (WorkspaceGrid.Visibility != Visibility.Visible) return;
        // Let the compiled binding and list layout apply before restoring offsets and focus.
        await Task.Yield();
        if (_closed || generation != _documentGeneration || key != _workspaceContextKey) return;
        QuestionList.UpdateLayout();
        FindVisualChild<ScrollViewer>(QuestionList)?.ChangeView(null, context.ListY, null, true);
        QuestionDetail.ChangeView(null, context.ContentY, null, true);
        switch (context.Focus)
        {
            case "notes":
                NotesEditor.Focus(FocusState.Programmatic);
                NotesEditor.Select(Math.Min(context.SelectionStart, NotesEditor.Text.Length), Math.Min(context.SelectionLength, Math.Max(0, NotesEditor.Text.Length - context.SelectionStart)));
                break;
            case "list": QuestionList.Focus(FocusState.Programmatic); break;
            case "source": SourceViewer.Focus(FocusState.Programmatic); break;
            case "content": QuestionContentHost.Focus(FocusState.Programmatic); break;
        }
        UpdateSourceHighlight();
    }

    private static bool IsWithin(DependencyObject? child, DependencyObject parent)
    {
        while (child is not null)
        {
            if (ReferenceEquals(child, parent)) return true;
            child = VisualTreeHelper.GetParent(child);
        }
        return false;
    }
    private static T? FindVisualChild<T>(DependencyObject element) where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T match) return match;
            if (FindVisualChild<T>(child) is { } found) return found;
        }
        return null;
    }
}
