using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private bool _changingLibrary;

    private async void SelectLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_changingLibrary || _modelRequestRunning) return;
        try
        {
            var folder = await PickFolderAsync();
            if (folder is null) return;
            if (!File.Exists(LegacyLibraryLocation.DatabaseFile(folder.Path)))
            { LibraryFeedback.Text = "此文件夹没有资料库，请选择之前的保存位置。"; return; }
            if (!await CanOpenExistingLibraryAsync(folder.Path))
            { LibraryFeedback.Text = "请从资料页的更多菜单选择“恢复备份”或“导入旧版资料”。"; return; }
            if (Path.GetFullPath(folder.Path).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(_libraryDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) && _library is not null) return;
            _changingLibrary = true; Navigation.IsEnabled = false;
            if (await SwitchLibraryAsync(folder.Path))
            {
                Navigation.SelectedItem = Navigation.SettingsItem;
                LibraryFeedback.Text = "已打开资料库。";
            }
        }
        catch { LibraryFeedback.Text = "无法打开，请检查文件夹并结束当前任务后重试。"; }
        finally { _changingLibrary = false; Navigation.IsEnabled = true; }
    }

    private async Task<bool> CanOpenExistingLibraryAsync(string directory)
    {
        if (!File.Exists(Path.Combine(directory, ".native-writer.lock"))) return false;
        await using var existing = await LibraryRepository.OpenAsync(new(directory, LibraryOpenMode.ReadOnly), _lifetime.Token);
        return (await existing.InspectAsync(_lifetime.Token)).SchemaVersion == 2;
    }

    private async void WorkspaceNotice_CloseButtonClick(InfoBar sender, object args)
    {
        if (sender.Title != "发现旧版资料" || _library is null) return;
        try { await ChangeSettingsAsync(current => current with { LegacyNoticeDismissed = true }); }
        catch { ViewModel.StatusMessage = "提示已关闭，但未能记住此选择。"; }
    }

    private async void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(_libraryDirectory);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder)) LibraryFeedback.Text = "无法打开文件夹，请按上方路径查找。";
        }
        catch { LibraryFeedback.Text = "无法打开文件夹，请检查保存位置。"; }
    }

    private async void ChangeLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_changingLibrary || _library is null) return;
        try
        {
            if (_modelRequestRunning) { LibraryFeedback.Text = "请等待模型请求结束后再更改位置。"; return; }
            if (_jobs is not null && (await _jobs.ListAsync()).Any(job => !job.State.IsTerminal()))
            { LibraryFeedback.Text = "请先结束当前任务。"; return; }
            if (ViewModel.HasUnsavedChanges || HasUnsavedSettingsChanges) { LibraryFeedback.Text = "请先保存修改后再更改位置。"; return; }
            var folder = await PickFolderAsync();
            if (folder is null) return;
            string destination = Path.Combine(folder.Path, "OhMyStudy资料库");
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = "更改资料库位置",
                Content = "现有资料将复制到以下位置，今后导入也保存在这里。原资料库会保留。\n\n" + destination,
                PrimaryButtonText = "复制并切换",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (Directory.Exists(destination) || File.Exists(destination))
            { LibraryFeedback.Text = "目标资料库已存在，请选择其他文件夹。"; return; }
            await CopyAndSwitchLibraryAsync(destination);
        }
        catch { LibraryFeedback.Text = _library is null ? "无法打开资料库，请重新启动应用。" : "更改失败，当前资料仍可使用。请检查目标位置与剩余空间。"; }
    }

    private async Task CopyAndSwitchLibraryAsync(string destination)
    {
        if (_changingLibrary) throw new InvalidOperationException();
        _changingLibrary = true;
        bool returnToSettings = SettingsPanel.Visibility == Visibility.Visible;
        Navigation.IsEnabled = false;
        bool importHeld = false;
        try
        {
            await _importGate.WaitAsync(_lifetime.Token); importHeld = true;
            await _settingsGate.WaitAsync(_lifetime.Token);
            try
            {
                var library = _library ?? throw new IOException();
                if (ViewModel.HasUnsavedChanges || HasUnsavedSettingsChanges || _jobs is not null && (await _jobs.ListAsync()).Any(job => !job.State.IsTerminal()))
                    throw new InvalidOperationException();
                LibraryFeedback.Text = "正在复制资料…";
                await library.BackupAsync(destination, _lifetime.Token);
                if (!await SwitchLibraryAsync(destination)) { LibraryFeedback.Text = "已保留副本，未切换资料库。"; return; }
                LibraryFeedback.Text = "保存位置已更改，原资料库已保留。";
            }
            finally { _settingsGate.Release(); }
        }
        finally
        {
            if (importHeld) _importGate.Release();
            _changingLibrary = false; Navigation.IsEnabled = true;
            if (returnToSettings) Navigation.SelectedItem = Navigation.SettingsItem;
        }
    }
}
