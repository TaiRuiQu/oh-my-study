using Microsoft.UI.Xaml;
using OhMyStudy.Domain;

namespace OhMyStudy.App.ViewModels;

public static class OutcomeVisuals
{
    public static string Label(Verdict? verdict) => verdict switch
    {
        Verdict.Correct => UiText.Get("Correct"),
        Verdict.Partial => UiText.Get("Partially correct"),
        Verdict.Incorrect => UiText.Get("Incorrect"),
        Verdict.Unanswered => UiText.Get("Unanswered"),
        Verdict.Uncertain => UiText.Get("Needs review"),
        _ => ""
    };

    public static string Glyph(Verdict? verdict) => verdict switch
    {
        Verdict.Correct => "\uE73E",
        Verdict.Partial => "\uE7BA",
        Verdict.Incorrect => "\uE711",
        Verdict.Unanswered => "\uE738",
        _ => "\uE9CE"
    };

    public static Style ChipStyle(Verdict? verdict) => Resource(Tone(verdict) + "ChipStyle");
    public static Style IconStyle(Verdict? verdict) => Resource(Tone(verdict) + "IconStyle");

    public static string Score(double? earned, double? possible) => (earned, possible) switch
    {
        ({ } e, { } p) => string.Format(UiText.Culture, "{0:0.##} / {1:0.##}", e, p),
        (null, { } p) => UiText.Format("{0:g} points", p),
        _ => ""
    };

    private static string Tone(Verdict? verdict) => verdict switch
    {
        Verdict.Correct => "Correct",
        Verdict.Partial => "Partial",
        Verdict.Incorrect or Verdict.Unanswered => "Incorrect",
        Verdict.Uncertain => "Review",
        _ => "Neutral"
    };

    private static Style Resource(string key) => (Style)Microsoft.UI.Xaml.Application.Current.Resources[key];
}
