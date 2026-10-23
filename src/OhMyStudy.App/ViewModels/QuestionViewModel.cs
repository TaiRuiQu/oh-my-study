using OhMyStudy.Domain;

namespace OhMyStudy.App.ViewModels;

public interface IQuestionPreview
{
    string PreviewText { get; }
}

public sealed partial class QuestionViewModel : ObservableObject, IQuestionPreview
{
    private string _notes = "";
    private readonly string? _presentationJson;

    public QuestionViewModel(int number, string statement, bool incorrect, int page)
    {
        Number = number;
        PreviewText = statement;
        IsIncorrect = incorrect;
        Page = page;
    }

    public QuestionViewModel(SavedQuestion saved, Document? document = null)
    {
        Saved = saved;
        _presentationJson = DomainJson.Serialize(saved.Question);
        Number = int.TryParse(saved.Question.Number, out int n) ? n : 0;
        PreviewText = Preview(saved.Question);
        _notes = saved.Question.Notes;
        IsIncorrect = saved.Question.IsMistake;
        var source = saved.Question.Sources.FirstOrDefault();
        Page = (int)(document?.Pages.FirstOrDefault(p => p.Id == source?.PageId)?.PageIndex ?? 0) + 1;
    }

    public SavedQuestion? Saved { get; private set; }
    public int Number { get; }
    public string Label => Saved is { } saved ? UiText.Format("Question {0}", saved.Question.Number) : Number == 0 ? UiText.Get("Questions") : UiText.Format("Question {0}", Number);
    public string PreviewText { get; }
    public bool IsIncorrect { get; }
    public int Page { get; }
    public string OutcomeLabel => Saved is { } saved ? OutcomeVisuals.Label(saved.Question.Outcome)
        : Number == 0 ? "" : IsIncorrect ? OutcomeVisuals.Label(Verdict.Partial) : OutcomeVisuals.Label(Verdict.Correct);
    public string Notes
    {
        get => _notes;
        set { if (Set(ref _notes, value)) Raise(nameof(IsDirty)); }
    }
    public bool IsDirty => Saved is { } saved && Notes != saved.Question.Notes;
    public bool Matches(SavedQuestion saved) => Saved is { } current && current.Id == saved.Id
        && current.UpdatedAt == saved.UpdatedAt && _presentationJson == DomainJson.Serialize(saved.Question);
    public void AcceptSaved(SavedQuestion saved, string? submittedNotes = null)
    {
        bool retainNotes = submittedNotes is not null && _notes != submittedNotes;
        Saved = saved;
        if (!retainNotes) _notes = saved.Question.Notes;
        Raise(nameof(Notes)); Raise(nameof(IsDirty));
    }
    public double SourceY => 228 + ((Number - 1) % 3) * 206;
    public static QuestionViewModel Empty() => new(0, "", false, 1);

    internal static string Preview(Question question)
    {
        string text = QuestionText.Preview(question);
        if (string.IsNullOrWhiteSpace(text) && question.Stem.Concat(question.Items.SelectMany(item => item.Prompt)).Any(content => content is ImageContent)) return UiText.Get("Image question");
        return string.IsNullOrWhiteSpace(text) ? UiText.Get("No question content yet") : text;
    }
}
