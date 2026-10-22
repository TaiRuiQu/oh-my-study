using System.Globalization;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.App.ViewModels;

public sealed class PaperViewModel(PaperSummary summary)
{
    public PaperSummary Summary { get; } = summary;
    public string Id => Summary.Document.Id;
    public string Title => Summary.Document.Name;
    public string Subject => Summary.Subject ?? UiText.Get("未分类");
    public string QuestionCount => UiText.Format("{0} 道题", Summary.QuestionCount);
    public string Imported => DateTimeOffset.TryParse(Summary.Document.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.ToLocalTime().ToString("g", UiText.Culture) : UiText.Get("时间未知");
    public string Score => $"{Summary.Earned?.ToString("0.##") ?? "—"} / {Summary.Possible?.ToString("0.##") ?? "—"}";
}
