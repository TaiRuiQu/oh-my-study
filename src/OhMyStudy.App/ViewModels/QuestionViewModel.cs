using OhMyStudy.Domain;

namespace OhMyStudy.App.ViewModels;

public sealed partial class QuestionViewModel : ObservableObject
{
    private string _answer;
    private string _notes = "";
    private readonly string? _presentationJson;

    public QuestionViewModel(int number, string statement, string answer, bool incorrect, int page)
    {
        Number = number;
        Statement = statement;
        PreviewText = statement;
        _answer = answer;
        IsIncorrect = incorrect;
        Page = page;
    }

    public QuestionViewModel(SavedQuestion saved, Document? document = null)
    {
        Saved = saved;
        _presentationJson = DomainJson.Serialize(saved.Question);
        Number = int.TryParse(saved.Question.Number, out int n) ? n : 0;
        Statement = PlainText(saved.Question);
        PreviewText = Preview(saved.Question);
        _answer = saved.Question.StandardAnswer;
        _notes = saved.Question.Explanation;
        IsIncorrect = IsMistake(saved.Question);
        var source = saved.Question.Sources.FirstOrDefault();
        Page = (int)(document?.Pages.FirstOrDefault(p => p.Id == source?.PageId)?.PageIndex ?? 0) + 1;
    }

    public SavedQuestion? Saved { get; private set; }
    public int Number { get; }
    public string Label => Saved is { } saved ? $"第 {saved.Question.Number} 题" : Number == 0 ? "题目" : $"第 {Number} 题";
    public string Statement { get; }
    public string PreviewText { get; }
    public bool IsIncorrect { get; }
    public int Page { get; }
    public string SourceLabel => $"第 {Page} 页";
    public string OutcomeLabel => Saved is { } saved ? saved.Question.Attempt?.Grade.Verdict switch
    { Verdict.Correct => "✓ 正确", Verdict.Partial => "△ 部分正确", Verdict.Incorrect => "× 错误", Verdict.Unanswered => "○ 未作答", Verdict.Uncertain => "? 待确认", _ => "" }
        : Number == 0 ? "" : IsIncorrect ? "△ 部分正确" : "✓ 正确";
    public string Answer { get => _answer; set => Set(ref _answer, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public bool IsDirty => Saved is { } saved && (Answer != saved.Question.StandardAnswer || Notes != saved.Question.Explanation);
    public bool Matches(SavedQuestion saved) => Saved is { } current && current.Id == saved.Id
        && current.UpdatedAt == saved.UpdatedAt && _presentationJson == DomainJson.Serialize(saved.Question);
    public void AcceptSaved(SavedQuestion saved, Question? submitted = null)
    {
        // A save can finish while the user is already typing the next change.
        bool retainAnswer = submitted is not null && _answer != submitted.StandardAnswer;
        bool retainNotes = submitted is not null && _notes != submitted.Explanation;
        Saved = saved;
        if (!retainAnswer) _answer = saved.Question.StandardAnswer;
        if (!retainNotes) _notes = saved.Question.Explanation;
        Raise(nameof(Answer)); Raise(nameof(Notes)); Raise(nameof(IsDirty));
    }
    public double SourceY => 228 + ((Number - 1) % 3) * 206;
    public Question EditedQuestion => (Saved ?? throw new InvalidOperationException("示例无法保存。")).Question with { StandardAnswer = Answer, Explanation = Notes };
    public static QuestionViewModel Empty() => new(0, "", "", false, 1);
    public static bool IsMistake(Question question) => question.Attempt?.Grade.Verdict is Verdict.Partial or Verdict.Incorrect or Verdict.Unanswered || question.Parts.Any(IsMistake);
    public static string PlainText(Question question) => string.Join("\n", question.Content.Select(content => content switch
    { TextContent text => text.Text, FormulaContent formula => formula.Latex, TableContent table => string.Join("\n", table.Rows.Select(row => string.Join("  ", row))), ImageContent image => image.Caption, _ => "" }));

    private static string Preview(Question question)
    {
        string text = string.Concat(question.Content.Take(8).Select(content => content switch
        {
            TextContent prose => prose.Text,
            FormulaContent formula => formula.Latex.Trim() is var latex && (latex.StartsWith('$') || latex.StartsWith(@"\(", StringComparison.Ordinal) || latex.StartsWith(@"\[", StringComparison.Ordinal))
                ? latex : @"\(" + formula.Latex + @"\)",
            TableContent table => " " + string.Join("　", table.Rows.Take(2).SelectMany(row => row)),
            ImageContent image => string.IsNullOrWhiteSpace(image.Caption) ? "图片题" : image.Caption,
            _ => ""
        }));
        return string.IsNullOrWhiteSpace(text) ? "暂无题目内容" : text;
    }
}
