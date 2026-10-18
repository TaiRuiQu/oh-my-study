using OhMyStudy.Domain;

namespace OhMyStudy.App.ViewModels;

public sealed partial class QuestionViewModel : ObservableObject
{
    private string _answer;
    private string _notes = "";

    public QuestionViewModel(int number, string statement, string answer, bool incorrect, int page)
    {
        Number = number;
        Statement = statement;
        _answer = answer;
        IsIncorrect = incorrect;
        Page = page;
    }

    public QuestionViewModel(SavedQuestion saved, Document? document = null)
    {
        Saved = saved;
        Number = int.TryParse(saved.Question.Number, out int n) ? n : 0;
        Statement = PlainText(saved.Question);
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
    public bool IsIncorrect { get; }
    public int Page { get; }
    public string SourceLabel => $"第 {Page} 页";
    public string OutcomeLabel => Saved is { } saved ? saved.Question.Attempt?.Grade.Verdict switch
    { Verdict.Correct => "✓ 正确", Verdict.Partial => "△ 部分正确", Verdict.Incorrect => "× 错误", Verdict.Unanswered => "○ 未作答", Verdict.Uncertain => "? 待确认", _ => "" }
        : Number == 0 ? "" : IsIncorrect ? "△ 部分正确" : "✓ 正确";
    public string Answer { get => _answer; set => Set(ref _answer, value); }
    public string Notes { get => _notes; set => Set(ref _notes, value); }
    public bool IsDirty => Saved is { } saved && (Answer != saved.Question.StandardAnswer || Notes != saved.Question.Explanation);
    public void AcceptSaved(SavedQuestion saved)
    { Saved = saved; _answer = saved.Question.StandardAnswer; _notes = saved.Question.Explanation; Raise(nameof(Answer)); Raise(nameof(Notes)); }
    public double SourceY => 228 + ((Number - 1) % 3) * 206;
    public Question EditedQuestion => (Saved ?? throw new InvalidOperationException("示例无法保存。")).Question with { StandardAnswer = Answer, Explanation = Notes };
    public static QuestionViewModel Empty() => new(0, "", "", false, 1);
    public static bool IsMistake(Question question) => question.Attempt?.Grade.Verdict is Verdict.Partial or Verdict.Incorrect or Verdict.Unanswered || question.Parts.Any(IsMistake);
    public static string PlainText(Question question) => string.Join("\n", question.Content.Select(content => content switch
    { TextContent text => text.Text, FormulaContent formula => formula.Latex, TableContent table => string.Join("\n", table.Rows.Select(row => string.Join("  ", row))), ImageContent image => image.Caption, _ => "" }));
}
