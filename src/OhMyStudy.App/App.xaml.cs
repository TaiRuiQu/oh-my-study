using Microsoft.UI.Xaml;
using OhMyStudy.App.Diagnostics;

namespace OhMyStudy.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _window;

    public App()
    {
        AppLog.Info("Application.Language", "Loading interface language.");
        string[] arguments = Environment.GetCommandLineArgs();
        int localizationSmoke = Array.IndexOf(arguments, "--localization-smoke");
        string? diagnosticLanguage = localizationSmoke > 0 && localizationSmoke + 2 < arguments.Length ? arguments[localizationSmoke + 1] : null;
        if (diagnosticLanguage is null && arguments.Any(argument => argument.StartsWith("--", StringComparison.Ordinal) && argument.Contains("smoke", StringComparison.Ordinal))) diagnosticLanguage = "zh-CN";
        string systemLanguage = Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault() ?? System.Globalization.CultureInfo.CurrentUICulture.Name;
        UiText.Initialize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMyStudy", "language.txt"), systemLanguage, diagnosticLanguage);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = UiText.Language;
        AppLog.Info("Application.Language", "Interface language loaded: " + UiText.Language);
        AppLog.Info("Application.Start", $"Version={typeof(App).Assembly.GetName().Version}; PID={Environment.ProcessId}; BaseDirectory={AppContext.BaseDirectory}");
        UnhandledException += (_, args) => SmokeStartupDiagnostics.Log("Application.UnhandledException", args.Exception);
        bool xamlDiagnostics = Array.IndexOf(Environment.GetCommandLineArgs(), "--smoke-test") >= 0;
#if DEBUG
        xamlDiagnostics = true;
#endif
        if (xamlDiagnostics)
        {
            DebugSettings.BindingFailed += (_, args) => SmokeStartupDiagnostics.LogText("XAML.BindingFailed", args.Message);
            DebugSettings.XamlResourceReferenceFailed += (_, args) => SmokeStartupDiagnostics.LogText("XAML.ResourceReferenceFailed", args.Message);
            DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
        }
        AppLog.Info("Application.InitializeComponent", "Loading application resources.");
        try
        {
            InitializeComponent();
            AppLog.Info("Application.ResourcesReady", "Application resources loaded.");
        }
        catch (Exception exception)
        {
            SmokeStartupDiagnostics.Log("Application.InitializeComponent", exception);
            throw;
        }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        string[] arguments = Environment.GetCommandLineArgs();
        string? reportPath = null;
        string? previewPdfPath = null;
        string? workflowReport = null;
        string? performanceReport = null;
        for (int index = 1; index < arguments.Length; index++)
        {
            if (arguments[index] == "--smoke-test" && index + 1 < arguments.Length)
                reportPath = Path.GetFullPath(arguments[++index]);
            else if (arguments[index] == "--preview-pdf" && index + 1 < arguments.Length)
                previewPdfPath = Path.GetFullPath(arguments[++index]);
            else if (arguments[index] == "--workflow-smoke" && index + 1 < arguments.Length)
                workflowReport = Path.GetFullPath(arguments[++index]);
            else if (arguments[index] == "--performance-smoke" && index + 1 < arguments.Length)
                performanceReport = Path.GetFullPath(arguments[++index]);
        }

        try
        {
            AppLog.Info("Application.OnLaunched", "Creating the main window.");
            var font = new Microsoft.UI.Xaml.Media.FontFamily(UiText.InterfaceFontFamily);
            Microsoft.UI.Xaml.Application.Current.Resources["ContentControlThemeFontFamily"] = font;
            _window = new MainWindow(reportPath, previewPdfPath, workflowReport, performanceReport);
            AppLog.Info("Application.WindowCreated", "Activating the main window.");
            _window.Activate();
            AppLog.Info("Application.WindowActivated", "Main window activated.");
        }
        catch (Exception exception)
        {
            SmokeStartupDiagnostics.Log("Application.OnLaunched", exception);
            throw;
        }
    }
}
