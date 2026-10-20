using System.Globalization;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.App.ViewModels;

public sealed class PaperViewModel(PaperSummary summary)
{
    public PaperSummary Summary { get; } = summary;
    public string Id => Summary.Document.Id;
    public string Title => Summary.Document.Name;
    public string Subject => Summary.Subject ?? "未分类";
    public string QuestionCount => $"{Summary.QuestionCount} 道题";
    public string Imported => DateTimeOffset.TryParse(Summary.Document.CreatedAt, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time) ? time.ToLocalTime().ToString("yyyy/MM/dd HH:mm") : "时间未知";
    public string Score => $"{Summary.Earned?.ToString("0.##") ?? "—"} / {Summary.Possible?.ToString("0.##") ?? "—"}";
}
