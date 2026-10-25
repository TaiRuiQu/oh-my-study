using Microsoft.UI.Xaml;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.App.ViewModels;

public sealed class QuestionSummaryViewModel(QuestionSummary summary) : IQuestionPreview
{
    public QuestionSummary Summary { get; } = summary;
    public string Id => Summary.Id;
    public string Title => UiText.Format("Question {0}", QuestionViewModel.DisplayNumber(Summary.Number));
    public string Exam => Summary.DocumentName;
    public string Details => string.Join(" · ", new[] { Summary.Subject, Summary.QuestionType, Summary.ItemCount > 1 ? UiText.Format("{0} parts", Summary.ItemCount) : "" }.Where(text => !string.IsNullOrWhiteSpace(text)));
    public Visibility DetailsVisibility => Details.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
    public string PreviewText => string.IsNullOrWhiteSpace(Summary.Preview) ? UiText.Get("No question content yet") : Summary.Preview;
    public string Score => OutcomeVisuals.Score(Summary.Earned, Summary.Possible);
    public string OutcomeLabel => OutcomeVisuals.Label(Summary.Verdict);
    public string OutcomeGlyph => OutcomeVisuals.Glyph(Summary.Verdict);
    public Style ChipStyle => OutcomeVisuals.ChipStyle(Summary.Verdict);
    public Style IconStyle => OutcomeVisuals.IconStyle(Summary.Verdict);
    public Visibility OutcomeVisibility => Summary.Verdict is null ? Visibility.Collapsed : Visibility.Visible;
    public Visibility ReviewVisibility => Summary.NeedsReview && Summary.Verdict != Verdict.Uncertain ? Visibility.Visible : Visibility.Collapsed;
    public string AccessibleName => string.Join(", ", new[] { Title, Exam, Details, Score, OutcomeLabel }.Where(text => !string.IsNullOrWhiteSpace(text)));
}
