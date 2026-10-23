using System.Text.Json.Nodes;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Diagnostics;
using Windows.Graphics;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task RunLocalizationSmokeAsync(string reportPath)
    {
        var captures = new JsonArray();
        var checks = new JsonObject();
        var report = new JsonObject { ["language"] = UiText.Language, ["checks"] = checks, ["screenshots"] = captures };
        int exitCode = 1;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
            RootGrid.RequestedTheme = ElementTheme.Light;
            _initialized = false;
            ExecutionModePicker.SelectedIndex = 2;
            _initialized = true;
            LibraryPath.Text = @"C:\Study\Library";
            LocalizeNavigationSettings();
            report["headingFont"] = PageHeading.FontFamily.Source;
            report["controlFont"] = LanguagePicker.FontFamily.Source;
            checks["font"] = PageHeading.FontFamily.Source == UiText.InterfaceFontFamily && LanguagePicker.FontFamily.Source == UiText.InterfaceFontFamily;
            checks["navigation"] = ((NavigationViewItem)Navigation.MenuItems[0]).Content?.ToString() == UiText.Get("Exams");
            checks["settings"] = WinRT.CastExtensions.As<NavigationViewItem>(Navigation.SettingsItem).Content?.ToString() == UiText.Get("Settings");
            checks["language"] = RootGrid.Language == UiText.Language && LanguagePicker.SelectedIndex > 0;
            checks["currency"] = ((ComboBoxItem)ModelCurrency.Items[0]).Content?.ToString() == UiText.Get("US dollar");
            checks["sort"] = ((ComboBoxItem)PaperSort.Items[0]).Content?.ToString() == UiText.Get("Newest first");
            foreach (int width in new[] { 1440, 820, 460 })
            {
                AppWindow.Resize(new SizeInt32(width, 960));
                Navigation.SelectedItem = Navigation.MenuItems[0];
                ShowPaperList();
                await Task.Delay(250);
                string papers = Path.ChangeExtension(reportPath, $".{width}.exams.png");
                await CaptureWorkspaceAsync(papers); captures.Add((JsonNode?)JsonValue.Create(papers));
                Navigation.SelectedItem = Navigation.SettingsItem;
                await Task.Delay(250);
                SettingsPanel.ChangeView(null, SettingsPanel.ScrollableHeight, null, true);
                await Task.Delay(100);
                string settings = Path.ChangeExtension(reportPath, $".{width}.settings.png");
                await CaptureWorkspaceAsync(settings); captures.Add((JsonNode?)JsonValue.Create(settings));
                checks["settings-width-" + width] = LanguagePicker.ActualWidth > 0 && LanguagePicker.ActualWidth <= SettingsPanel.ViewportWidth;
                checks["language-header-" + width] = LanguagePicker.Header?.ToString() == UiText.Get("Language");
                var themePosition = ThemePicker.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point());
                var languagePosition = LanguagePicker.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point());
                checks["appearance-row-" + width] = Math.Abs(themePosition.Y - languagePosition.Y) < 1
                    && languagePosition.X >= themePosition.X + ThemePicker.ActualWidth + 15;
                RootGrid.RequestedTheme = ElementTheme.Dark;
                await Task.Delay(150);
                string darkSettings = Path.ChangeExtension(reportPath, $".{width}.settings-dark.png");
                await CaptureWorkspaceAsync(darkSettings); captures.Add((JsonNode?)JsonValue.Create(darkSettings));
                RootGrid.RequestedTheme = ElementTheme.Light;
            }
            checks["sample-content-preserved"] = UiText.ActivityText("中文 English 日本語 한국어 \\(x^2\\)") == "中文 English 日本語 한국어 \\(x^2\\)";
            bool passed = checks.All(pair => pair.Value!.GetValue<bool>());
            report["status"] = passed ? "passed" : "failed";
            exitCode = passed ? 0 : 1;
        }
        catch (Exception error)
        {
            report["status"] = "failed";
            report["error"] = error.ToString();
            AppLog.Error("Localization.Smoke", error);
        }
        finally
        {
            await File.WriteAllTextAsync(reportPath, report.ToJsonString());
            Close();
            Environment.Exit(exitCode);
        }
    }
}
