using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OhMyStudy.App.Localization;

internal static class LocalizationChecks
{
    public static void Run(Action<string, Action> check)
    {
        check("language_resolution", () =>
        {
            Require(UiText.ResolveLanguage("zh-Hans-CN") == "zh-CN");
            Require(UiText.ResolveLanguage("en-GB") == "en-US");
            Require(UiText.ResolveLanguage("ja") == "ja-JP");
            Require(UiText.ResolveLanguage("ko-KR") == "ko-KR");
            Require(UiText.ResolveLanguage("fr-FR") == "en-US");
            Require(UiText.ResolvePreference("ja-JP", "zh-CN") == "ja-JP");
            Require(UiText.ResolvePreference("system", "zh-TW") == "zh-CN");
            Require(UiText.ResolvePreference("invalid", "en-US") == "en-US");
        });
        var originalCulture = CultureInfo.CurrentUICulture;
        var originalDefaultCulture = CultureInfo.DefaultThreadCurrentUICulture;
        var directory = Directory.CreateTempSubdirectory("OhMyStudy-Localization-").FullName;
        string path = Path.Combine(directory, "language.txt");
        try
        {
            var source = Catalog("zh-CN");
            check("xaml_catalog_keys", () =>
            {
                using var stream = typeof(UiText).Assembly.GetManifestResourceStream("Localization.MainWindow.xaml")!;
                using var reader = new StreamReader(stream);
                var markup = reader.ReadToEnd();
                foreach (Match match in Regex.Matches(markup, @"\{l:Text Key=(S[0-9]+)\}")) Require(source.ContainsKey(match.Groups[1].Value));
            });
            foreach (string language in new[] { "zh-CN", "en-US", "ja-JP", "ko-KR" })
            {
                check("catalog_" + language, () =>
                {
                    var translated = Catalog(language);
                    Require(source.Count == translated.Count && source.Keys.All(translated.ContainsKey));
                    UiText.SavePreferenceAsync(path, language).GetAwaiter().GetResult();
                    UiText.Initialize(path, "fr-FR");
                    Require(UiText.Language == language && UiText.Culture.Name == language);
                    foreach (var pair in source)
                    {
                        Require(translated[pair.Key].Length > 0);
                        Require(UiText.Get(pair.Value) == translated[pair.Key]);
                        Require(UiText.GetByKey(pair.Key) == translated[pair.Key]);
                        var expected = Placeholders(pair.Value);
                        Require(expected.SequenceEqual(Placeholders(translated[pair.Key])));
                        if (expected.Length > 0)
                            Require(CompositeFormat.Parse(pair.Value).MinimumArgumentCount == CompositeFormat.Parse(translated[pair.Key]).MinimumArgumentCount);
                    }
                    Require(UiText.Get("unknown source") == "unknown source");
                    Require(UiText.GetByKey("unknown key") == "unknown key");
                    Require(UiText.Format("第 {0} / {1} 页", 2, 12).Contains("12", StringComparison.Ordinal));
                    Require(!string.IsNullOrEmpty(UiText.InterfaceFontFamily));
                    if (language is "zh-CN" or "en-US") Require(UiText.ReadingFontFamily == "Microsoft YaHei UI");
                    Require(UiText.ActivityText("已阅读 2 页，已保存 15 题。") == UiText.Format("已阅读 {0} 页，已保存 {1} 题。", "2", "15"));
                    Require(UiText.ActivityText("阅读第 12 页") == UiText.Format("阅读第 {0} 页", "12"));
                    Require(UiText.ActivityText("第 4 轮识别") == UiText.Format("第 {0} 轮识别", "4"));
                    Require(UiText.ActivityText("已生成 3 项操作") == UiText.Format("已生成 {0} 项操作", "3"));
                    Require(UiText.ActivityText("中文 English 日本語 한국어") == "中文 English 日本語 한국어");
                });
            }
            check("preference_persistence_and_fallback", () =>
            {
                UiText.SavePreferenceAsync(path, "en-US").GetAwaiter().GetResult();
                UiText.Initialize(path, "zh-CN");
                Require(UiText.Language == "en-US" && UiText.Get("设置") == "Settings");
                UiText.SavePreferenceAsync(path, "system").GetAwaiter().GetResult();
                UiText.Initialize(path, "ko-KR");
                Require(UiText.Language == "ko-KR" && UiText.Preference == "system");
                File.WriteAllText(path, "broken preference");
                UiText.Initialize(path, "ja-JP");
                Require(UiText.Language == "ja-JP" && UiText.Preference == "system");
                File.Delete(path);
                UiText.Initialize(path, "zh-CN");
                Require(UiText.Language == "zh-CN");
                try { UiText.SavePreferenceAsync(path, "invalid").GetAwaiter().GetResult(); throw new InvalidOperationException("Invalid preference accepted."); }
                catch (ArgumentException) { }
                Require(!Directory.EnumerateFiles(directory, "*.tmp").Any());
            });
        }
        finally
        {
            CultureInfo.CurrentUICulture = originalCulture;
            CultureInfo.DefaultThreadCurrentUICulture = originalDefaultCulture;
            if (File.Exists(path)) File.Delete(path);
            Directory.Delete(directory);
        }
    }

    private static Dictionary<string, string> Catalog(string language)
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("OhMyStudy.App.Localization." + language + ".xml")!;
        return XDocument.Load(stream).Root!.Elements("text").ToDictionary(element => (string)element.Attribute("key")!, element => element.Value, StringComparer.Ordinal);
    }

    private static string[] Placeholders(string value)
        => Regex.Matches(value, @"(?<!\{)\{\d+(?:,-?\d+)?(?::[^{}]+)?\}(?!\})").Select(match => match.Value).Order(StringComparer.Ordinal).ToArray();

    private static void Require(bool condition)
    {
        if (!condition) throw new InvalidOperationException("Localization regression failed.");
    }
}
