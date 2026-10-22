using System.Collections.ObjectModel;
using OhMyStudy.Domain;

namespace OhMyStudy.App.ViewModels;

public sealed partial class WorkspaceViewModel : ObservableObject, IDisposable
{
    private readonly List<QuestionViewModel> _allQuestions = [];
    private readonly Dictionary<string, QuestionViewModel> _editing = new(StringComparer.Ordinal);
    private QuestionViewModel _selectedQuestion = QuestionViewModel.Empty();
    private CancellationTokenSource? _taskCancellation;
    private string _taskStatus = UiText.Get("Ready");
    private string _statusMessage = "";
    private double _taskProgress;
    private bool _isTaskRunning;
    private string _sectionTitle = UiText.Get("Question bank");
    private string _documentTitle = UiText.Get("All questions");
    private string _section = "library";
    private bool _saving;
    public WorkspaceViewModel(bool sample = false)
    {
        CancelTaskCommand = new ActionCommand(CancelTask, () => IsTaskRunning);
        FinishEditCommand = new ActionCommand(async () => await SaveAsync(), () => !_saving);
        ResetAnswerCommand = new ActionCommand(() => SelectedQuestion.Answer = UiText.Get("The maximum is 2 and the minimum is −2."));
        if (sample) { _section = "materials"; _sectionTitle = UiText.Get("Exams"); LoadSample(); }
    }

    public bool IsSample { get; private set; }
    public bool HasUnsavedChanges => _editing.Values.Any(question => question.IsDirty);
    public Document? CurrentDocument { get; private set; }
    public ObservableCollection<QuestionViewModel> Questions { get; } = [];
    public ActionCommand CancelTaskCommand { get; }
    public ActionCommand FinishEditCommand { get; }
    public ActionCommand ResetAnswerCommand { get; }
    public Func<QuestionViewModel, Task>? SaveQuestionAsync { get; set; }
    public Action? CancelCurrentJob { get; set; }
    public QuestionViewModel SelectedQuestion
    {
        get => _selectedQuestion;
        set
        {
            if (value is null || ReferenceEquals(_selectedQuestion, value)) return;
            _selectedQuestion.PropertyChanged -= SelectedQuestionChanged;
            Set(ref _selectedQuestion, value);
            _selectedQuestion.PropertyChanged += SelectedQuestionChanged;
            if (value.IsDirty) StatusMessage = UiText.Get("You have unsaved changes.");
        }
    }
    private void SelectedQuestionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    { if (SelectedQuestion.IsDirty) StatusMessage = UiText.Get("You have unsaved changes."); }
    public string SectionTitle { get => _sectionTitle; set { if (Set(ref _sectionTitle, value)) Raise(nameof(PageTitle)); } }
    public string DocumentTitle { get => _documentTitle; set { if (Set(ref _documentTitle, value)) Raise(nameof(PageTitle)); } }
    public string PageTitle => _section is "settings" or "tasks" or "papers" ? SectionTitle : DocumentTitle;
    public string TaskStatus { get => _taskStatus; private set => Set(ref _taskStatus, value); }
    public string StatusMessage { get => _statusMessage; set => Set(ref _statusMessage, value); }
    public double TaskProgress { get => _taskProgress; private set => Set(ref _taskProgress, value); }
    public bool IsTaskRunning { get => _isTaskRunning; private set { if (Set(ref _isTaskRunning, value)) CancelTaskCommand.NotifyCanExecuteChanged(); } }
    public string CountLabel => UiText.Format("Questions: {0}", Questions.Count);

    public void LoadSample()
    {
        IsSample = true; CurrentDocument = null; DocumentTitle = UiText.Get("Functions and derivatives · Practice");
        string[] statements = [UiText.Get("Given f(x) = x³ − 3x, find its maximum and minimum on [−2, 2]."), UiText.Get("In Cartesian coordinates, the line y = kx + 1 meets the parabola y = x² at A and B. Find the length of AB."), UiText.Get("Given a₁ = 1 and aₙ₊₁ = 2aₙ + 1, find the general term of {aₙ} and justify your answer.")];
        string[] answers = [UiText.Get("The maximum is 2 and the minimum is −2."), "AB = √((k² + 4)(k² + 1))。", "aₙ = 2ⁿ − 1。"];
        _allQuestions.Clear();
        _allQuestions.AddRange(Enumerable.Range(1, 500).Select(n => new QuestionViewModel(n, statements[(n - 1) % 3], answers[(n - 1) % 3], n % 3 == 1, ((n - 1) % 12) + 1)));
        RefreshQuestions();
    }

    public void LoadDocument(Document document, IEnumerable<SavedQuestion> questions)
    {
        IsSample = false; CurrentDocument = document; DocumentTitle = document.Name;
        SetQuestions(questions.Select(q => new QuestionViewModel(q, document)));
    }
    public void LoadLibrary(IEnumerable<SavedQuestion> questions, bool mistakes)
    {
        IsSample = false; CurrentDocument = null; DocumentTitle = mistakes ? UiText.Get("Mistakes") : UiText.Get("All questions");
        SetQuestions(questions.Select(q => new QuestionViewModel(q)));
    }
    public void ClearLibrary()
    { IsSample = false; CurrentDocument = null; DocumentTitle = UiText.Get("My materials"); _editing.Clear(); SetQuestions([]); }
    private void SetQuestions(IEnumerable<QuestionViewModel> questions)
    {
        _allQuestions.Clear();
        foreach (var incoming in questions)
        {
            var item = incoming;
            if (incoming.Saved is { } saved)
            {
                if (_editing.TryGetValue(saved.Id, out var old) && (old.IsDirty || old.Page == incoming.Page && old.Matches(saved))) item = old;
                _editing[saved.Id] = item;
            }
            _allQuestions.Add(item);
        }
        RefreshQuestions();
    }
    private void RefreshQuestions()
    {
        string? selected = SelectedQuestion.Saved?.Id;
        var source = (_section == "mistakes" ? _allQuestions.Where(q => q.IsIncorrect) : _allQuestions).ToList();
        // Retain realized rows, selection and input focus when a task updates another question.
        for (int i = 0; i < source.Count; i++)
        {
            if (i < Questions.Count && ReferenceEquals(Questions[i], source[i])) continue;
            int existing = Questions.IndexOf(source[i]);
            if (existing >= 0) Questions.Move(existing, i);
            else Questions.Insert(i, source[i]);
        }
        while (Questions.Count > source.Count) Questions.RemoveAt(Questions.Count - 1);
        SelectedQuestion = Questions.FirstOrDefault(q => q.Saved?.Id == selected) ?? Questions.FirstOrDefault() ?? QuestionViewModel.Empty();
        Raise(nameof(CountLabel));
    }
    public void ShowSection(string tag)
    {
        _section = tag;
        Raise(nameof(PageTitle));
        SectionTitle = tag switch { "library" => UiText.Get("Question bank"), "mistakes" => UiText.Get("Mistakes"), "settings" => UiText.Get("Settings"), "tasks" => UiText.Get("Tasks"), _ => UiText.Get("Exams") };
        if (tag != "settings" && tag != "tasks") RefreshQuestions();
    }
    private async Task SaveAsync()
    {
        if (IsSample) { StatusMessage = UiText.Get("Modified, not saved yet."); return; }
        if (SelectedQuestion.Saved is null || SaveQuestionAsync is null) return;
        if (_saving) return;
        _saving = true; FinishEditCommand.NotifyCanExecuteChanged();
        try { await SaveQuestionAsync(SelectedQuestion); StatusMessage = HasUnsavedChanges ? UiText.Get("You have unsaved changes.") : UiText.Get("Saved."); }
        catch { StatusMessage = UiText.Get("Could not save. Wait for the task to finish and retry."); }
        finally { _saving = false; FinishEditCommand.NotifyCanExecuteChanged(); }
    }
    public void UpdateJob(Job? job)
    {
        IsTaskRunning = job is not null && !job.State.IsTerminal();
        TaskProgress = job?.Progress ?? 0;
        TaskStatus = job is null ? UiText.Get("Ready") : UiText.ActivityText(job.Message);
    }

    internal async Task RunSyntheticTaskAsync()
    {
        if (IsTaskRunning) return;
        using var cancellation = new CancellationTokenSource(); _taskCancellation = cancellation;
        IsTaskRunning = true; TaskProgress = 0; TaskStatus = UiText.Get("Running");
        try
        {
            for (int step = 1; step <= 20; step++) { await Task.Delay(220, cancellation.Token); TaskProgress = step * 5; TaskStatus = $"{step * 5}%"; }
            TaskStatus = UiText.Get("Completed");
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { TaskStatus = UiText.Get("Canceled"); }
        finally { _taskCancellation = null; IsTaskRunning = false; }
    }
    public void CancelTask() { _taskCancellation?.Cancel(); CancelCurrentJob?.Invoke(); }
    public void Dispose() => CancelTask();
}
