using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using OhMyStudy.App.ViewModels;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Execution;
using OhMyStudy.Infrastructure.Storage;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task RunWorkflowSmokeAsync(string reportPath)
    {
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        var timings = new List<double>();
        var clock = Stopwatch.StartNew();
        var root = Path.Combine(Path.GetDirectoryName(reportPath)!, "synthetic-workflow-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        string? error = null;
        int exitCode = 1;
        try
        {
            await InitializeLibraryAsync(Path.Combine(root, "library"));
            checks["empty-start"] = _library is not null && !ViewModel.IsSample && ViewModel.Questions.Count == 0;
            if (_library is null) throw new InvalidOperationException("Synthetic library did not initialize");
            var bytes = await MakeSyntheticPngAsync();
            var sources = Enumerable.Range(0, 12).Select(n => new ImportSource($"第{n + 1}页.png", bytes)).ToArray();
            var document = await _library.ImportDocumentAsync("函数与导数 · 练习整理", ImportMode.Blank, sources);
            await RefreshDocumentsAsync(document.Id);
            checks["import-twelve-pages"] = _documents.Count == 1 && document.Pages.Count == 12 && ViewModel.CurrentDocument?.Id == document.Id && !ViewModel.IsSample;
            checks["original-bytes"] = (await _library.ReadAssetAsync(document.Pages[0].AssetId)).Bytes.AsSpan().SequenceEqual(bytes);
            for (int i = 0; i < 12; i++) await ShowSourcePageAsync(i);
            checks["all-pages-displayed"] = _sourcePageIndex == 11 && ImportedImage.Source is not null && PdfPageLabel.Text == "第 12 / 12 页";
            await ShowSourcePageAsync(0);
            await RotateSourceAsync(90);
            checks["rotation-persisted"] = (await _library.GetDocumentAsync(document.Id)).Pages[0].Rotation == 90;
            await RotateSourceAsync(-90);
            document = await _library.GetDocumentAsync(document.Id);

            var region = new Region { PageId = document.Pages[0].Id, X = .1, Y = .16, Width = .8, Height = .2 };
            var question = new Question
            {
                DraftKey = "workflow-q1",
                Number = "1",
                Subject = "数学",
                QuestionType = "计算",
                Content = [new TextContent { Text = "已知函数 f(x) = x³ − 3x，求函数在区间 [−2, 2] 上的最大值与最小值。" }, new FormulaContent { Latex = @"f'(x)=3x^2-3" }],
                Options = [],
                Points = null,
                StandardAnswer = "最大值为2，最小值为−2。",
                Explanation = "比较端点与驻点的函数值。",
                KnowledgePoints = ["导数"],
                Sources = [region],
                Uncertainties = [],
                Attempt = null,
                Parts = []
            };
            var provider = new Provider { Id = Guid.NewGuid().ToString(), Name = "synthetic", Protocol = Protocol.Responses, BaseUrl = "https://synthetic.invalid/", Models = [] };
            var model = new ModelConfig { Id = "synthetic", Capabilities = Capabilities.Unknown, Currency = "USD" };
            var job = new Job
            {
                Id = Guid.NewGuid().ToString(),
                Kind = DiagnosticKind.Grading,
                State = JobState.Running,
                CreatedAt = 1,
                UpdatedAt = 1,
                Progress = 0,
                Message = "synthetic",
                Sequence = 1,
                ExecutionPolicy = ExecutionPolicy.Freeze(ExecutionMode.RequestPermission, SandboxCapability.Detect(), false),
                Input = new GradingInput { Document = document, Provider = provider, Model = model, AllowUnknown = false }
            };
            JobEvent Event(Job snapshot, string kind) => new() { Id = snapshot.Id + "-" + snapshot.Sequence, JobId = snapshot.Id, Sequence = snapshot.Sequence, CreatedAt = snapshot.UpdatedAt, Kind = kind, Payload = DomainJson.ToElement(snapshot) };
            await _library.SaveJobEventAsync(job, Event(job, "started"));
            await _library.BeginGradingAsync(job);
            var saved = await _library.SaveGradingQuestionAsync(job.Id, question);
            job = job with { State = JobState.Completed, Sequence = 2, UpdatedAt = 2, Progress = 100 };
            await _library.SaveJobEventAsync(job, Event(job, "completed"));
            await ReloadQuestionsAsync();
            checks["question-loaded"] = ViewModel.Questions.Count == 1 && ViewModel.SelectedQuestion.Saved?.Id == saved.Id;
            ViewModel.SelectedQuestion.Answer = "最大值为 2，最小值为 −2。";
            ViewModel.SelectedQuestion.Notes = "已核对端点和驻点。";
            await ViewModel.SaveQuestionAsync!(ViewModel.SelectedQuestion);
            ViewModel.StatusMessage = "已保存。";
            var restored = (await _library.GradingQuestionsAsync(document.Id)).Single();
            checks["edit-saved"] = restored.Question.StandardAnswer == "最大值为 2，最小值为 −2。" && restored.Question.Explanation == "已核对端点和驻点。";
            UpdateSourceHighlight();
            checks["source-highlight"] = SourceOverlay.Visibility == Visibility.Visible && Math.Abs(SourceHighlight.Width - .8 * _pageWidth) < .1;
            Navigation.SelectedItem = Navigation.MenuItems[1]; await Task.Delay(100);
            checks["library-native-navigation"] = ViewModel.Questions.Count == 1;
            Navigation.SelectedItem = Navigation.MenuItems[2]; await Task.Delay(100);
            checks["blank-no-false-mistake"] = ViewModel.Questions.Count == 0;
            Navigation.SelectedItem = Navigation.MenuItems[0]; await Task.Delay(150);
            for (int i = 0; i < 20; i++)
            {
                var measure = Stopwatch.StartNew(); await ShowSourcePageAsync(i % 12); timings.Add(measure.Elapsed.TotalMilliseconds);
            }
            checks["cache-byte-budget"] = _pageCache.RetainedBytes <= 64L * 1024 * 1024;
            var backup = Path.Combine(root, "backup"); await _library.BackupAsync(backup);
            var copy = Path.Combine(root, "restored"); await LibraryRepository.RestoreBackupAsync(backup, copy);
            await using (var reopened = await LibraryRepository.OpenAsync(new(copy)))
            {
                var stored = (await reopened.GradingQuestionsAsync(document.Id)).Single();
                checks["backup-reopen-edit"] = stored.Question.StandardAnswer == restored.Question.StandardAnswer && (await reopened.GetDocumentAsync(document.Id)).Pages.Count == 12;
            }
            // A background refresh or navigation must retain an unfinished edit until explicit save.
            ViewModel.SelectedQuestion.Notes = "未保存的草稿";
            await ReloadQuestionsAsync();
            checks["dirty-refresh-keeps-edit"] = ViewModel.SelectedQuestion.Notes == "未保存的草稿" && ViewModel.SelectedQuestion.IsDirty;
            ViewModel.SelectedQuestion.Notes = restored.Question.Explanation;
            ViewModel.StatusMessage = "已保存。";
            await ShowSourcePageAsync(0);
            RootGrid.RequestedTheme = ElementTheme.Dark; await Task.Delay(100);
            checks["dark-caption"] = AppWindow.TitleBar.ButtonForegroundColor == CaptionForeground;
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".dark.png"));
            RootGrid.RequestedTheme = ElementTheme.Light; await Task.Delay(100);
            checks["light-caption"] = AppWindow.TitleBar.ButtonForegroundColor == CaptionForeground;
            checks["keyboard-editor-focus"] = AnswerEditor.Focus(FocusState.Programmatic);
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".png"));
            checks["native-aot"] = !RuntimeFeature.IsDynamicCodeSupported && !Process.GetCurrentProcess().Modules.Cast<ProcessModule>().Any(m => m.ModuleName.Equals("coreclr.dll", StringComparison.OrdinalIgnoreCase));
            exitCode = checks.Values.All(value => value) ? 0 : 1;
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            using var process = Process.GetCurrentProcess();
            var ordered = timings.Order().ToArray();
            var report = new WorkflowReport(DateTimeOffset.UtcNow, exitCode == 0, root, checks, error, clock.Elapsed.TotalMilliseconds,
                ordered.Length == 0 ? 0 : ordered[(int)Math.Ceiling(ordered.Length * .95) - 1], process.PrivateMemorySize64, _pageCache.RetainedBytes,
                ["clean-Windows-matrix", "real-provider-semantics", "physical-printing", "DPI-IME-accessibility", "reference-device-frame-time-and-cold-start-P95"]);
            File.WriteAllText(reportPath, JsonSerializer.Serialize(report, WorkflowJson.Default.WorkflowReport));
            if (_jobs is not null) await _jobs.DisposeAsync(); _jobs = null;
            if (_library is not null) await _library.DisposeAsync(); _library = null;
            _shutdownFinished = true;
            Microsoft.UI.Xaml.Application.Current.Exit();
            Environment.Exit(exitCode);
        }
    }

    private static async Task<byte[]> MakeSyntheticPngAsync()
    {
        const uint width = 900, height = 1200;
        byte[] pixels = new byte[width * height * 4];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int i = (y * (int)width + x) * 4;
            byte shade = y is > 180 and < 380 && x is > 90 and < 810 && (y % 22 is < 3) ? (byte)65 : (byte)250;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = shade; pixels[i + 3] = 255;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels);
        await encoder.FlushAsync();
        using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size);
        var data = new byte[stream.Size]; reader.ReadBytes(data); return data;
    }
}

internal sealed record WorkflowReport(DateTimeOffset TimestampUtc, bool Passed, string SyntheticRoot, Dictionary<string, bool> Checks, string? Error,
    double ScenarioElapsedMilliseconds, double CachedPageP95Milliseconds, long PrivateBytesAtEnd, long CacheRetainedBytes, string[] NotRun);
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(WorkflowReport))]
internal sealed partial class WorkflowJson : JsonSerializerContext;
