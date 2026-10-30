using Microsoft.UI.Xaml;
using OhMyStudy.App.Diagnostics;
using Windows.Storage.Pickers;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async void TaskExportRecognition_Click(object sender, RoutedEventArgs e)
    {
        if (_library is not { } library || _selectedTask is not { } job) return;
        TaskExportRecognition.IsEnabled = false;
        try
        {
            var picker = new FileSavePicker { SuggestedFileName = "recognition-materials" };
            picker.FileTypeChoices.Add("ZIP", [".zip"]);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            using var buffer = new MemoryStream();
            await library.ExportWorkflowArtifactsAsync(job.Id, buffer, _lifetime.Token);
            buffer.Position = 0;
            await using var stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            await buffer.CopyToAsync(stream, _lifetime.Token);
            ViewModel.StatusMessage = UiText.Get("Recognition materials exported.");
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Workflow.Export", error); ViewModel.StatusMessage = UiText.Get("Could not export recognition materials. Check that OCR completed and the destination is writable."); }
        finally { TaskExportRecognition.IsEnabled = true; }
    }
}
