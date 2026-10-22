using Microsoft.UI.Xaml.Controls;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private bool _updatingLanguage;
    private static string LanguagePreferencePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMyStudy", "language.txt");

    private void InitializeLanguagePicker()
    {
        _updatingLanguage = true;
        LanguagePicker.SelectedIndex = UiText.Preference switch { "zh-CN" => 1, "en-US" => 2, "ja-JP" => 3, "ko-KR" => 4, _ => 0 };
        RootGrid.Language = UiText.Language;
        _updatingLanguage = false;
    }

    private async void LanguagePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || _updatingLanguage || LanguagePicker.SelectedItem is not ComboBoxItem { Tag: string preference }) return;
        LanguagePicker.IsEnabled = false;
        try
        {
            await UiText.SavePreferenceAsync(LanguagePreferencePath, preference, _lifetime.Token);
            LanguageFeedback.Text = UiText.Get("语言已保存，重新启动应用后生效。未保存的修改请先保存。");
        }
        catch (OperationCanceledException) when (_closed) { }
        catch
        {
            InitializeLanguagePicker();
            LanguageFeedback.Text = UiText.Get("语言设置未能保存，请重试。");
        }
        finally { LanguagePicker.IsEnabled = true; }
    }
}
