using System.Globalization;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.App.ViewModels;

public sealed class PaperViewModel(PaperSummary summary)
{
    public PaperSummary Summary { get; } = summary;
    public string Id => Summary.Document.Id;
    public string Title => Summary.Document.Name;
    public string Subject => Summary.Subject ?? UiText.Get("Uncategorized");
    public string QuestionCount => UiText.Format("Questions: {0}", Summary.QuestionCount);
    public string Imported => DateTimeOffset.TryParse(Summary.Document.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.ToLocalTime().ToString("g", UiText.Culture) : UiText.Get("Time unknown");
    public string Score => $"{Summary.Earned?.ToString("0.##") ?? "—"} / {Summary.Possible?.ToString("0.##") ?? "—"}";
}
