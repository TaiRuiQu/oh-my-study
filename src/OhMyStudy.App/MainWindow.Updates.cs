using System.Reflection;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Diagnostics;
using Velopack;
using Velopack.Sources;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private Action? _restartAfterClose;

    private async Task ShowAboutAndUpdatesAsync()
    {
        string version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
            ?? typeof(App).Assembly.GetName().Version?.ToString() ?? UiText.Get("Unknown");
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = UiText.Format("Version {0}", version), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["QuietTextStyle"] });
        content.Children.Add(feedback);
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Oh My Study",
            RequestedTheme = RootGrid.ActualTheme,
            Content = content,
            PrimaryButtonText = UiText.Get("Check for updates"),
            CloseButtonText = UiText.Get("Got it"),
            DefaultButton = ContentDialogButton.Close
        };
        UpdateManager? manager = null;
        UpdateInfo? update = null;
        bool downloaded = false;
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            try
            {
                manager ??= CreateUpdateManager(null);
                if (update is null)
                {
                    feedback.Text = UiText.Get("Checking for updates…");
                    update = await manager.CheckForUpdatesAsync();
                    if (update is null)
                    {
                        feedback.Text = UiText.Get("You are using the latest version.");
                        return;
                    }
                    feedback.Text = UiText.Format("Version {0} is available. Download it now?", update.TargetFullRelease.Version);
                    dialog.PrimaryButtonText = UiText.Get("Download");
                }
                else if (!downloaded)
                {
                    Action<int> progress = value => DispatcherQueue.TryEnqueue(() => feedback.Text = UiText.Format("Downloading update… {0}%", value));
                    feedback.Text = UiText.Format("Downloading update… {0}%", 0);
                    await manager.DownloadUpdatesAsync(update, progress);
                    downloaded = true;
                    feedback.Text = UiText.Get("Update downloaded. Save your work, then restart to install it.");
                    dialog.PrimaryButtonText = UiText.Get("Restart and install");
                    dialog.DefaultButton = ContentDialogButton.Primary;
                }
                else
                {
                    _restartAfterClose = () => manager.ApplyUpdatesAndRestart(update);
                    dialog.Hide();
                    Close();
                }
            }
            catch (Velopack.Exceptions.NotInstalledException)
            {
                feedback.Text = UiText.Get("Updates are available only for installed copies. Download a new installer to update a portable copy.");
            }
            catch (Exception error)
            {
                AppLog.Error(downloaded ? "Updates.Apply" : update is null ? "Updates.Check" : "Updates.Download", error);
                feedback.Text = UiText.Get(update is null ? "Could not check for updates. Check your connection and try again later." : "Could not download the update. Try again later.");
            }
            finally
            {
                dialog.IsPrimaryButtonEnabled = update is null && feedback.Text == UiText.Get("You are using the latest version.") ? false : true;
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    private static UpdateManager CreateUpdateManager(string? diagnosticSource)
        => diagnosticSource is not null
            ? new UpdateManager(diagnosticSource)
            : new UpdateManager(new GithubSource("https://github.com/TaiRuiQu/oh-my-study", null, false));

    internal async Task RunUpdateSmokeAsync(string reportPath, string source)
    {
        try
        {
            var manager = CreateUpdateManager(source);
            var update = await manager.CheckForUpdatesAsync();
            if (update is null)
            {
                string report = "{\"passed\":true,\"version\":\"" + manager.CurrentVersion + "\",\"downloaded\":true,\"restarted\":true}";
                await File.WriteAllTextAsync(reportPath, report);
                Microsoft.UI.Xaml.Application.Current.Exit();
                return;
            }
            await manager.DownloadUpdatesAsync(update);
            string downloadReport = "{\"passed\":true,\"version\":\"" + update.TargetFullRelease.Version + "\",\"downloaded\":true}";
            await File.WriteAllTextAsync(reportPath, downloadReport);
            manager.ApplyUpdatesAndRestart(update, Environment.GetCommandLineArgs().Skip(1).ToArray());
        }
        catch (Exception error)
        {
            AppLog.Error("Updates.Smoke", error);
            string report = "{\"passed\":false,\"error\":\"" + System.Text.Json.JsonEncodedText.Encode(error.ToString()) + "\"}";
            await File.WriteAllTextAsync(reportPath, report);
            Microsoft.UI.Xaml.Application.Current.Exit();
        }
    }
}
