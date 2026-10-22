using Microsoft.UI.Xaml.Markup;

namespace OhMyStudy.App.Localization;

[MarkupExtensionReturnType(ReturnType = typeof(string))]
public sealed partial class TextExtension : MarkupExtension
{
    public string Key { get; set; } = "";

    protected override object ProvideValue() => UiText.GetByKey(Key);
}
