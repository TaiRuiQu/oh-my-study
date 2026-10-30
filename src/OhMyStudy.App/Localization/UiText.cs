using System.Globalization;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using OhMyStudy.Domain;

namespace OhMyStudy.App.Localization;

public static partial class UiText
{
    private static readonly Dictionary<string, string> Sources = Load("en-US");
    private static readonly Dictionary<string, string> Keys = Sources.Concat(Load("zh-CN"))
        .GroupBy(pair => pair.Value, StringComparer.Ordinal)
        .ToDictionary(group => group.Key, group => group.First().Key, StringComparer.Ordinal);
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

    public static string ExamFormatName(ExamFormat format) => Get(format switch
    {
        ExamFormat.BlankPaper => "Exam without student answers",
        ExamFormat.InlineAnswers => "Answers on the exam paper",
        ExamFormat.MarkedInlineAnswers => "Teacher-marked answers on the exam paper",
        ExamFormat.SeparateAnswerSheet => "Exam with separate answer sheets",
        ExamFormat.MarkedAnswerSheet => "Exam with teacher-marked answer sheets",
        _ => "Exam format needs review."
    });

    public static string ActivityText(string source)
    {
        if (source == "pending") return Get("Queued");
        string translated = Get(source);
        if (translated != source) return translated;
        if (source.StartsWith("Question ", StringComparison.Ordinal)) return Format("Question {0}", source[9..]);
        foreach (var prefix in new[] { "Solve question", "Assess student work", "Process question" })
            if (source.StartsWith(prefix + " ", StringComparison.Ordinal)) return Format("{0} {1}", Get(prefix), source[(prefix.Length + 1)..]);
        var workflow = WorkflowProgress().Match(source);
        if (workflow.Success) return Format(workflow.Groups[1].Value switch { "OCR pages" => "OCR pages: {0}/{1}.", "Uploaded pages" => "Uploaded pages: {0}/{1}.", _ => "Questions completed: {0}/{1}." }, workflow.Groups[2].Value, workflow.Groups[3].Value);
        workflow = WorkflowGrouped().Match(source);
        if (workflow.Success) return Format(source.EndsWith("Solving independently.", StringComparison.Ordinal) ? "Questions grouped: {0}. Solving independently." : "Questions grouped: {0}.", workflow.Groups[1].Value);
        var match = ProgressMessage().Match(source);
        if (match.Success) return Format("Pages read: {0}. Questions saved: {1}.", match.Groups[1].Value, match.Groups[2].Value);
        match = RoundMessage().Match(source);
        if (match.Success) return Format("Recognition round {0}", match.Groups[1].Value);
        match = ReadPageMessage().Match(source);
        if (match.Success) return Format("Read page {0}", match.Groups[1].Value);
        match = PreparedActionsMessage().Match(source);
        return match.Success ? Format("Actions prepared: {0}", match.Groups[1].Value) : source;
    }

    [GeneratedRegex(@"\A(OCR pages|Questions completed|Uploaded pages): ([0-9]+)/([0-9]+)\.\z", RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowProgress();

    [GeneratedRegex(@"\AQuestions grouped: ([0-9]+)\.(?: Solving independently\.)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex WorkflowGrouped();

    [GeneratedRegex(@"\A(?:Pages read: |已阅读 )([0-9]+)(?:\. Questions saved: | 页，已保存 )([0-9]+)(?:\.| 题。)\z", RegexOptions.CultureInvariant)]
    private static partial Regex ProgressMessage();

    [GeneratedRegex(@"\A(?:Recognition round |第 )([0-9]+)(?: 轮识别)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex RoundMessage();

    [GeneratedRegex(@"\A(?:Read page |阅读第 )([0-9]+)(?: 页)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex ReadPageMessage();

    [GeneratedRegex(@"\A(?:Actions prepared: |已生成 )([0-9]+)(?: 项操作)?\z", RegexOptions.CultureInvariant)]
    private static partial Regex PreparedActionsMessage();

    private static Dictionary<string, string> Load(string language)
    {
        using var stream = typeof(UiText).Assembly.GetManifestResourceStream("OhMyStudy.App.Localization." + language + ".xml")
            ?? throw new InvalidOperationException("Missing translation catalog: " + language);
        return XDocument.Load(stream).Root!.Elements("text").ToDictionary(element => (string)element.Attribute("key")!, element => element.Value, StringComparer.Ordinal);
    }
}
