using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OhMyStudy.App.Localization;

public static partial class UiText
{
    private static readonly Dictionary<string, string> Sources = Load("zh-CN");
    private static readonly Dictionary<string, string> Keys = Sources.ToDictionary(pair => pair.Value, pair => pair.Key, StringComparer.Ordinal);
    private static Dictionary<string, string> _translations = Load("en-US");

    public static string Language { get; private set; } = "en-US";
    public static string Preference { get; private set; } = "system";
    public static CultureInfo Culture => CultureInfo.GetCultureInfo(Language);
    public static string InterfaceFontFamily => Language switch
    {
        "zh-CN" => "Microsoft YaHei UI",
        "ja-JP" => "Yu Gothic UI",
        "ko-KR" => "Malgun Gothic",
        _ => "Segoe UI"
    };
    public static string ReadingFontFamily => Language is "en-US" or "zh-CN" ? "Microsoft YaHei UI" : InterfaceFontFamily;

    public static string ResolveLanguage(string? language)
    {
        string primary = language?.Split('-')[0].ToLowerInvariant() ?? "";
        return primary switch { "zh" => "zh-CN", "ja" => "ja-JP", "ko" => "ko-KR", _ => "en-US" };
    }

    public static string ResolvePreference(string? preference, string systemLanguage)
        => preference is "zh-CN" or "en-US" or "ja-JP" or "ko-KR" ? preference : ResolveLanguage(systemLanguage);

    public static void Initialize(string preferencePath, string systemLanguage, string? languageOverride = null)
    {
        string preference = "system";
        try { if (File.Exists(preferencePath)) preference = File.ReadAllText(preferencePath).Trim(); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        Preference = preference is "zh-CN" or "en-US" or "ja-JP" or "ko-KR" ? preference : "system";
        if (languageOverride is "zh-CN" or "en-US" or "ja-JP" or "ko-KR") Preference = languageOverride;
        Language = ResolvePreference(Preference, systemLanguage);
        _translations = Load(Language);
        CultureInfo.CurrentUICulture = Culture;
        CultureInfo.DefaultThreadCurrentUICulture = Culture;
    }

    public static async Task SavePreferenceAsync(string path, string preference, CancellationToken cancellationToken = default)
    {
        if (preference is not ("system" or "zh-CN" or "en-US" or "ja-JP" or "ko-KR")) throw new ArgumentException(nameof(preference));
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temporary, preference, cancellationToken).ConfigureAwait(false);
            File.Move(temporary, path, overwrite: true);
            Preference = preference;
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public static string Get(string source)
        => Keys.TryGetValue(source, out string? key) ? GetByKey(key) : source;

    public static string GetByKey(string key)
        => _translations.TryGetValue(key, out string? value) && value.Length > 0
            ? value : Sources.GetValueOrDefault(key, key);

    public static string Format(string source, params object?[] arguments)
        => string.Format(Culture, Get(source), arguments);

    public static string ActivityText(string source)
    {
        string translated = Get(source);
        if (translated != source || Language == "zh-CN") return translated;
        var match = ProgressMessage().Match(source);
        if (match.Success) return Format("已阅读 {0} 页，已保存 {1} 题。", match.Groups[1].Value, match.Groups[2].Value);
        match = RoundMessage().Match(source);
        if (match.Success) return Format("第 {0} 轮识别", match.Groups[1].Value);
        match = ReadPageMessage().Match(source);
        if (match.Success) return Format("阅读第 {0} 页", match.Groups[1].Value);
        match = PreparedActionsMessage().Match(source);
        return match.Success ? Format("已生成 {0} 项操作", match.Groups[1].Value) : source;
    }

    [GeneratedRegex(@"\A已阅读 ([0-9]+) 页，已保存 ([0-9]+) 题。\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressMessage();

    [GeneratedRegex(@"\A第 ([0-9]+) 轮识别\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoundMessage();

    [GeneratedRegex(@"\A阅读第 ([0-9]+) 页\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReadPageMessage();

    [GeneratedRegex(@"\A已生成 ([0-9]+) 项操作\z", RegexOptions.CultureInvariant)]
    private static partial Regex PreparedActionsMessage();

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("OhMyStudy.App.Localization." + language + ".xml")
            ?? throw new InvalidOperationException("Missing translation catalog: " + language);
        return XDocument.Load(stream).Root!.Elements("text").ToDictionary(element => (string)element.Attribute("key")!, element => element.Value, StringComparer.Ordinal);
    }
}
