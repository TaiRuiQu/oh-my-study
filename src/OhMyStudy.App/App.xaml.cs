using Microsoft.UI.Xaml;
using OhMyStudy.App.Diagnostics;

namespace OhMyStudy.App;

public partial class App : Microsoft.UI.Xaml.Application
{
    private MainWindow? _window;

    public App()
    {
        string systemLanguage = Windows.System.UserProfile.GlobalizationPreferences.Languages.FirstOrDefault() ?? System.Globalization.CultureInfo.CurrentUICulture.Name;
        UiText.Initialize(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMyStudy", "language.txt"), systemLanguage);
        Microsoft.Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = UiText.Language;
        AppLog.Info("Application.Start", $"Version={typeof(App).Assembly.GetName().Version}; PID={Environment.ProcessId}; Language={UiText.Language}; BaseDirectory={AppContext.BaseDirectory}");
        UnhandledException += (_, args) => AppLog.Error("Application.UnhandledException", args.Exception);
#if DEBUG
        DebugSettings.BindingFailed += (_, args) => AppLog.Failure("XAML.BindingFailed", args.Message);
        DebugSettings.XamlResourceReferenceFailed += (_, args) => AppLog.Failure("XAML.ResourceReferenceFailed", args.Message);
        DebugSettings.IsXamlResourceReferenceTracingEnabled = true;
#endif
        try { InitializeComponent(); }
        catch (Exception exception) { AppLog.Error("Application.InitializeComponent", exception); throw; }
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            var font = new Microsoft.UI.Xaml.Media.FontFamily(UiText.InterfaceFontFamily);
            Microsoft.UI.Xaml.Application.Current.Resources["ContentControlThemeFontFamily"] = font;
            _window = new MainWindow();
            _window.Activate();
            AppLog.Info("Application.WindowActivated", "Main window activated.");
        }
        catch (Exception exception) { AppLog.Error("Application.OnLaunched", exception); throw; }
    }
}
