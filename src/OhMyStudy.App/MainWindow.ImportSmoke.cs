using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Execution;
using OhMyStudy.Infrastructure.Jobs;
using OhMyStudy.Infrastructure.Storage;
using Windows.Storage;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    // Explicit isolated regression entry point. It never reads credentials or contacts a model.
    private async Task RunImportSmokeAsync(string reportPath)
    {
        string root = Path.Combine(Path.GetDirectoryName(reportPath)!, "import-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _libraryPointerOverride = Path.Combine(root, "active-library.txt");
        var checks = new List<string>();
        int exitCode = 1;
        try
        {
            Require(Thread.CurrentThread.GetApartmentState() == ApartmentState.STA && DispatcherQueue.HasThreadAccess, "ui-thread-sta");
            await InitializeLibraryAsync(Path.Combine(root, "library"));
            await ShowQuestionLibraryAsync();
            Require(_library is not null && ViewModel.Questions.Count == 0 && SourcePane.Visibility == Visibility.Collapsed, "empty-library");
            await Capture("empty");
            var setupChoice = ChooseProcessingAsync(false, importing: true);
            await CaptureDialog("setup-dialog");
            Require(await setupChoice is null && (await _library!.ListDocumentsAsync()).Count == 0, "missing-model-no-false-import");
            await _jobs!.DisposeAsync();
            _jobs = await JobQueue.CreateAsync(new LibraryJobStore(_library!), new LoggedJobRunner(new ImportSmokeRunner(_library!)),
                new PolicyToolExecutor(new HostToolExecutor(root), new SandboxToolExecutor(root)), Path.Combine(root, "task-results"));
            _ = ObserveJobsAsync();
            string providerId = Guid.NewGuid().ToString();
            var provider = new Provider { Id = providerId, Name = "示例供应商", Protocol = Protocol.Responses, BaseUrl = "https://synthetic.invalid/", KeyRef = "OhMyStudy/provider/" + providerId, Models = [] };
            var model = new ModelConfig { Id = "synthetic", Capabilities = Capabilities.Unknown, Currency = "USD" };
            var processing = new ProcessingChoice(provider, model, null, true, ExecutionPolicy.Freeze(ExecutionMode.RequestPermission, SandboxCapability.Detect(), false), ImportMode.Blank);
            string pdf = Path.Combine(root, "示例试卷.pdf");
            await File.WriteAllBytesAsync(pdf, CreateSmokePdf());
            await ImportFilesAsync(new[] { await StorageFile.GetFileFromPathAsync(pdf) }, confirmedProcessing: processing);
            await WaitForQuestions(3);
            Require(ViewModel.CurrentDocument is null && ViewModel.Questions.Count == 3 && SourcePane.Visibility == Visibility.Collapsed, "pdf-import-auto-library");
            Require((await _library!.ListDocumentsAsync()).Single().Pages.Count == 2, "pdf-two-pages-prepared");
            await Capture("questions");
            var savedQuestions = await _library.ListQuestionsAsync();
            ViewModel.LoadLibrary(savedQuestions.Take(1), false); await Capture("single");
            ViewModel.LoadLibrary(savedQuestions, false);
            Navigation.SelectedItem = Navigation.MenuItems[2]; await Capture("empty-mistakes");
            await ShowQuestionLibraryAsync();
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("dark");
            RootGrid.RequestedTheme = ElementTheme.Light;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 960)); await Capture("narrow");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("compact");
            var selected = ViewModel.SelectedQuestion;
            string answer = selected.Answer;
            selected.Answer = string.Join("\n", Enumerable.Repeat("比较端点与驻点的函数值，并逐项检查最大值与最小值。", 12));
            await Capture("long-answer"); selected.Answer = answer;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            string png = Path.Combine(root, "示例图片.png");
            await File.WriteAllBytesAsync(png, await MakeSyntheticPngAsync());
            await ImportFilesAsync(new[] { await StorageFile.GetFileFromPathAsync(png) }, confirmedProcessing: processing);
            await WaitForQuestions(6);
            Require(ViewModel.Questions.Count == 6 && (await _library.ListQuestionsAsync()).Count == 6, "image-import-appends-questions");
            await Capture("multiple");
            var first = (await _library.ListDocumentsAsync()).First();
            await LoadDocumentAsync(first);
            Require(SourcePane.Visibility == Visibility.Collapsed, "paper-no-default-preview");
            await Capture("paper");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("paper-compact");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            ViewOriginal_Click(this, new RoutedEventArgs()); await Task.Delay(500);
            Require(SourcePane.Visibility == Visibility.Visible && ImportedImage.Source is not null, "original-on-demand");
            await Capture("original");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("original-compact");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            CloseOriginal_Click(this, new RoutedEventArgs());
            string invalid = Path.Combine(root, "损坏.pdf");
            await File.WriteAllBytesAsync(invalid, Encoding.ASCII.GetBytes("%PDF-1.4\ninvalid"));
            int count = (await _library.ListDocumentsAsync()).Count;
            await ImportFilesAsync(new[] { await StorageFile.GetFileFromPathAsync(invalid) }, confirmedProcessing: processing);
            Require(ViewModel.StatusMessage.StartsWith("导入失败", StringComparison.Ordinal) && (await _library.ListDocumentsAsync()).Count == count && ImportPaperButton.IsEnabled, "invalid-pdf-recovers");
            await Capture("error");
            await ShowQuestionLibraryAsync();
            await Capture("recovered");
            await _library.UpsertProviderAsync(provider with { Models = [model] });
            await RefreshProvidersAsync();
            var importChoice = ChooseProcessingAsync(false, importing: true);
            await CaptureDialog("import-dialog");
            Require(await importChoice is null, "import-dialog-cancel");
            RootGrid.RequestedTheme = ElementTheme.Dark;
            var darkChoice = ChooseProcessingAsync(false, importing: true);
            await CaptureDialog("import-dialog-dark"); await darkChoice;
            RootGrid.RequestedTheme = ElementTheme.Light;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960));
            var compactChoice = ChooseProcessingAsync(false, importing: true);
            await CaptureDialog("import-dialog-compact"); await compactChoice;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            About_Click(this, new RoutedEventArgs()); await CaptureDialog("about");
            exitCode = 0;
            if (Environment.GetCommandLineArgs().Contains("--inspect-ui", StringComparer.Ordinal))
            {
                await File.WriteAllLinesAsync(reportPath, checks.Prepend("PASS"));
                AppWindow.Show(); Activate();
                await Task.Delay(TimeSpan.FromMinutes(10));
            }

            void Require(bool condition, string name)
            {
                if (!condition) throw new InvalidOperationException(name);
                checks.Add(name); AppLog.Info("ImportSmoke.Pass", name);
            }
            async Task Capture(string name)
            {
                await Task.Delay(220);
                await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + ".png"));
            }
            async Task CaptureDialog(string name)
            {
                await Task.Delay(300);
                var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().Single();
                await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + ".png"), dialog);
                dialog.Hide();
            }
            async Task WaitForQuestions(int count)
            {
                for (int attempt = 0; attempt < 60; attempt++)
                {
                    await Task.Delay(100);
                    if ((_jobs is null || (await _jobs.ListAsync()).All(j => j.State.IsTerminal())) && ViewModel.Questions.Count == count) return;
                }
                throw new InvalidOperationException($"Expected {count} questions, found {ViewModel.Questions.Count}.");
            }
        }
        catch (Exception error) { AppLog.Error("ImportSmoke", error); checks.Add("FAIL: " + error); }
        finally
        {
            await File.WriteAllLinesAsync(reportPath, checks.Prepend(exitCode == 0 ? "PASS" : "FAIL"));
            if (_jobs is not null) await _jobs.DisposeAsync(); _jobs = null;
            if (_library is not null) await _library.DisposeAsync(); _library = null;
            _shutdownFinished = true;
            Microsoft.UI.Xaml.Application.Current.Exit();
            Environment.Exit(exitCode);
        }
    }

    private sealed class ImportSmokeRunner(LibraryRepository library) : IJobRunner
    {
        public async Task RunAsync(Job job, JobContext context)
        {
            await context.CommitAsync(token => library.BeginGradingAsync(job, token));
            for (int index = 1; index <= 3; index++)
            {
                var question = new Question
                {
                    DraftKey = "import-q" + index,
                    Number = index.ToString(),
                    Subject = "数学",
                    QuestionType = "计算",
                    Content = [new TextContent { Text = index == 1 ? "已知函数 f(x) = x³ − 3x，求函数在区间 [−2, 2] 上的最大值与最小值。" : "解答第 " + index + " 题，并说明计算过程。" }],
                    Options = [],
                    Points = null,
                    StandardAnswer = "最大值为 2，最小值为 −2。",
                    Explanation = "比较端点与驻点的函数值。",
                    KnowledgePoints = ["导数"],
                    Sources = [new Region { PageId = job.Input!.Document.Pages[0].Id, X = .1, Y = .1, Width = .8, Height = .3 }],
                    Uncertainties = [],
                    Attempt = null,
                    Parts = []
                };
                await context.CommitAsync(token => library.SaveGradingQuestionAsync(job.Id, question, token));
                await context.ProgressAsync(index * 30, $"已保存 {index} 题。");
            }
        }
    }

    private static byte[] CreateSmokePdf()
    {
        string[] objects = ["<< /Type /Catalog /Pages 2 0 R >>", "<< /Type /Pages /Count 2 /Kids [3 0 R 4 0 R] >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 595 842] >>", "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 842 595] >>"];
        var text = new StringBuilder("%PDF-1.4\n"); var offsets = new List<int> { 0 };
        foreach (var obj in objects) { offsets.Add(text.Length); text.Append($"{offsets.Count - 1} 0 obj\n{obj}\nendobj\n"); }
        int xref = text.Length;
        text.Append($"xref\n0 {offsets.Count}\n0000000000 65535 f \n");
        foreach (int offset in offsets.Skip(1)) text.Append($"{offset:0000000000} 00000 n \n");
        text.Append($"trailer\n<< /Size {offsets.Count} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        return Encoding.ASCII.GetBytes(text.ToString());
    }
}
