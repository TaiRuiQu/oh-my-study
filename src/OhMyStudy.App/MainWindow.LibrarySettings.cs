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
            { LibraryFeedback.Text = UiText.Get("This folder has no library. Choose your previous save location."); return; }
            if (!await CanOpenExistingLibraryAsync(folder.Path))
            { LibraryFeedback.Text = UiText.Get("Choose Restore backup or Import legacy materials from the material page's More menu."); return; }
            if (Path.GetFullPath(folder.Path).TrimEnd(Path.DirectorySeparatorChar).Equals(
                Path.GetFullPath(_libraryDirectory).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) && _library is not null) return;
            _changingLibrary = true; Navigation.IsEnabled = false;
            if (await SwitchLibraryAsync(folder.Path))
            {
                Navigation.SelectedItem = Navigation.SettingsItem;
                LibraryFeedback.Text = UiText.Get("Library opened.");
            }
        }
        catch { LibraryFeedback.Text = UiText.Get("Could not open. Check the folder and finish the current task before retrying."); }
        finally { _changingLibrary = false; Navigation.IsEnabled = true; }
    }

    private async Task<bool> CanOpenExistingLibraryAsync(string directory)
    {
        if (!File.Exists(Path.Combine(directory, ".native-writer.lock"))) return false;
        await using var existing = await LibraryRepository.OpenAsync(new(directory, LibraryOpenMode.ReadOnly), _lifetime.Token);
        return (await existing.InspectAsync(_lifetime.Token)).SchemaVersion is >= 2 and <= 4;
    }

    private async void WorkspaceNotice_CloseButtonClick(InfoBar sender, object args)
    {
        if (sender.Title != UiText.Get("Legacy materials found") || _library is null) return;
        try { await ChangeSettingsAsync(current => current with { LegacyNoticeDismissed = true }); }
        catch { ViewModel.StatusMessage = UiText.Get("Notice dismissed, but the choice could not be saved."); }
    }

    private async void OpenLibraryFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await Windows.Storage.StorageFolder.GetFolderFromPathAsync(_libraryDirectory);
            if (!await Windows.System.Launcher.LaunchFolderAsync(folder)) LibraryFeedback.Text = UiText.Get("Could not open the folder. Use the path shown above.");
        }
        catch { LibraryFeedback.Text = UiText.Get("Could not open the folder. Check the save location."); }
    }

    private async void ChangeLibrary_Click(object sender, RoutedEventArgs e)
    {
        if (_changingLibrary || _library is null) return;
        try
        {
            if (_modelRequestRunning) { LibraryFeedback.Text = UiText.Get("Wait for the model request to finish before changing location."); return; }
            if (_jobs is not null && (await _jobs.ListAsync()).Any(job => !job.State.IsTerminal()))
            { LibraryFeedback.Text = UiText.Get("Finish the current task first."); return; }
            if (ViewModel.HasUnsavedChanges || HasUnsavedSettingsChanges) { LibraryFeedback.Text = UiText.Get("Save your changes before changing location."); return; }
            var folder = await PickFolderAsync();
            if (folder is null) return;
            string destination = Path.Combine(folder.Path, "OhMyStudy资料库");
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = UiText.Get("Change library location"),
                Content = UiText.Get("Materials will be copied to the location below, which will also store future imports. The original library will be preserved.\n\n") + destination,
                PrimaryButtonText = UiText.Get("Copy and switch"),
                CloseButtonText = UiText.Get("Cancel"),
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            if (Directory.Exists(destination) || File.Exists(destination))
            { LibraryFeedback.Text = UiText.Get("The destination library already exists. Choose another folder."); return; }
            await CopyAndSwitchLibraryAsync(destination);
        }
        catch { LibraryFeedback.Text = _library is null ? UiText.Get("Could not open the library. Restart the app.") : UiText.Get("Location change failed. Current materials remain usable. Check the destination and free space."); }
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
                LibraryFeedback.Text = UiText.Get("Copying materials…");
                await library.BackupAsync(destination, _lifetime.Token);
                if (!await SwitchLibraryAsync(destination)) { LibraryFeedback.Text = UiText.Get("Copy preserved. Library was not switched."); return; }
                LibraryFeedback.Text = UiText.Get("Save location changed. The original library is preserved.");
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
