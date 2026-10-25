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
        var feedback = new TextBlock { TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed };
        var downloadProgress = new ProgressBar { Minimum = 0, Maximum = 100, Visibility = Visibility.Collapsed };
        var content = new StackPanel { Spacing = 12 };
        content.Children.Add(new TextBlock { Text = UiText.Format("Version {0}", version), Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["QuietTextStyle"] });
        content.Children.Add(feedback);
        content.Children.Add(downloadProgress);
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
            feedback.Visibility = Visibility.Visible;
            try
            {
                manager ??= CreateUpdateManager();
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
                    Action<int> progress = value => DispatcherQueue.TryEnqueue(() =>
                    {
                        feedback.Text = UiText.Format("Downloading update… {0}%", value);
                        downloadProgress.Value = value;
                    });
                    feedback.Text = UiText.Format("Downloading update… {0}%", 0);
                    downloadProgress.Value = 0;
                    downloadProgress.Visibility = Visibility.Visible;
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
                downloadProgress.Visibility = Visibility.Collapsed;
                dialog.IsPrimaryButtonEnabled = update is null && feedback.Text == UiText.Get("You are using the latest version.") ? false : true;
                deferral.Complete();
            }
        };
        await dialog.ShowAsync();
    }

    private static UpdateManager CreateUpdateManager()
        => new(new GithubSource("https://github.com/TaiRuiQu/oh-my-study", null, false));

}
