using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Data.Sqlite;
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
        _libraryPointerOverride = Path.Combine(root, "active-library.txt");
        string? error = null;
        int exitCode = 1;
        try
        {
            await InitializeLibraryAsync(Path.Combine(root, "library"));
            checks["empty-start"] = _library is not null && !ViewModel.IsSample && ViewModel.Questions.Count == 0;
            if (_library is null) throw new InvalidOperationException("Synthetic library did not initialize");
            ExecutionModePicker.SelectedIndex = 0; SandboxNetworkToggle.IsChecked = true; await Task.Delay(50);
            var executionSettings = await _library.GetSettingsAsync();
            checks["sandbox-network-setting-saved"] = executionSettings.SandboxNetworking && executionSettings.ExecutionMode == "request_permission";
            SandboxNetworkToggle.IsChecked = false; await Task.Delay(50);
            checks["sandbox-network-default-restored"] = !(await _library.GetSettingsAsync()).SandboxNetworking;
            await File.WriteAllTextAsync(_libraryPointerOverride, _libraryDirectory);
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
            var complex = restored.Question with
            {
                Options = ["A. 最大值为2", "B. 最大值为3"],
                Content = [.. restored.Question.Content, new TableContent { Rows = [["x", "f(x)"], ["−2", "−2"]] },
                    new ImageContent { Caption = "函数图像", ContainsHandwriting = false, Region = region }],
                Parts = [question with { DraftKey = "workflow-part", Number = "(1)", Parts = [question with { DraftKey = "workflow-nested", Number = "①" }] }]
            };
            var complexEditor = new QuestionFields(complex, document);
            checks["complex-editor-roundtrip"] = DomainJson.Serialize(complexEditor.Read()) == DomainJson.Serialize(complex);
            var editorDialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "编辑题目", CloseButtonText = "关闭", Content = new ScrollViewer { Content = complexEditor.Panel, MaxHeight = 600 } };
            var editorShown = editorDialog.ShowAsync(); await Task.Delay(80);
            var partSection = FindEditorSection(complexEditor.Panel, "小问 (1)");
            if (partSection is not null) partSection.IsExpanded = true;
            await Task.Yield();
            var nestedSection = FindEditorSection(complexEditor.Panel, "小问 ①");
            if (nestedSection is not null) nestedSection.IsExpanded = true;
            await Task.Yield();
            bool nestedEdited = nestedSection?.Content is FrameworkElement nestedFields && SetEditorText(nestedFields, "标准答案", "嵌套小问答案");
            SetEditorText(complexEditor.Panel, "文字", "已核对函数题干。");
            SetEditorText(complexEditor.Panel, "标准答案", "编辑后的参考答案");
            editorDialog.Hide(); await editorShown;
            var complexSaved = await _library.EditQuestionAsync(saved.Id, complexEditor.Read());
            await ReloadQuestionsAsync();
            checks["complex-editor-preserves-structure"] = complexSaved.Question.Content.OfType<FormulaContent>().Count() == 1
                && complexSaved.Question.Content.OfType<TableContent>().Single().Rows[1][1] == "−2"
                && complexSaved.Question.Content.OfType<ImageContent>().Single().Region == region
                && complexSaved.Question.Options.Count == 2 && complexSaved.Question.Parts.Single().Parts.Single().Number == "①"
                && complexSaved.Question.Sources.Single() == region && complexSaved.Question.Attempt is null
                && complexSaved.Question.StandardAnswer == "编辑后的参考答案"
                && nestedEdited && complexSaved.Question.Parts.Single().Parts.Single().StandardAnswer == "嵌套小问答案";
            var invalidEditor = new QuestionFields(complexSaved.Question, document);
            SetEditorText(invalidEditor.Panel, "满分（未知留空）", "-1");
            bool invalidRejected = false;
            try { await _library.EditQuestionAsync(saved.Id, invalidEditor.Read()); }
            catch (DomainValidationException) { invalidRejected = true; }
            checks["invalid-edit-keeps-saved-content"] = invalidRejected
                && (await _library.GradingQuestionsAsync(document.Id)).Single().Question.StandardAnswer == "编辑后的参考答案";
            var inFlightDraft = new QuestionViewModel(complexSaved, document) { Answer = "提交的答案" };
            var submitted = inFlightDraft.EditedQuestion;
            inFlightDraft.Answer = "保存期间继续输入";
            inFlightDraft.AcceptSaved(complexSaved with { Question = submitted }, submitted);
            checks["save-keeps-newer-typing"] = inFlightDraft.IsDirty && inFlightDraft.Answer == "保存期间继续输入";
            // Return to the original simple fixture so the remaining smoke checks keep their purpose.
            restored = await _library.EditQuestionAsync(saved.Id, restored.Question);
            await ReloadQuestionsAsync();
            UpdateSourceHighlight();
            checks["source-highlight"] = SourceOverlay.Visibility == Visibility.Visible && Math.Abs(SourceHighlight.Width - .8 * _pageWidth) < .1;
            Navigation.SelectedItem = Navigation.MenuItems[1]; await Task.Delay(100);
            checks["library-native-navigation"] = ViewModel.Questions.Count == 1;
            Navigation.SelectedItem = Navigation.MenuItems[2]; await Task.Delay(100);
            checks["blank-no-false-mistake"] = ViewModel.Questions.Count == 0;
            Navigation.SelectedItem = Navigation.MenuItems[0]; await Task.Delay(150);
            await ShowSourcePageAsync(0);
            SourceViewer.ChangeView(10, 18, 1.2f, true); await Task.Delay(140);
            RememberPageViewport();
            var firstPageView = _pageViewports[document.Pages[0].Id];
            await ShowSourcePageAsync(1);
            SourceViewer.ChangeView(0, 0, .65f, true); await Task.Delay(50);
            await ShowSourcePageAsync(0); await Task.Delay(160);
            checks["per-page-viewport"] = _sourcePageId == document.Pages[0].Id && Math.Abs(SourceViewer.ZoomFactor - firstPageView.Zoom) < .02;
            SourceViewer.ChangeView(null, null, 1.8f, true); ImproveReadingResolutionAsync();
            SourceViewer.ChangeView(null, null, 3.8f, true); ImproveReadingResolutionAsync();
            await Task.Delay(320);
            int largestLevel = _currentRasterLevel;
            await Task.Delay(150);
            checks["resolution-upgrade-does-not-regress"] = largestLevel >= 2400 && _currentRasterLevel >= largestLevel;
            await ShowSourcePageAsync(3);
            SourceViewer.ChangeView(45, 70, 1.1f, true); await Task.Delay(100);
            AnswerEditor.Focus(FocusState.Programmatic); AnswerEditor.Select(2, 3);
            RememberViewport();
            var storedView = _viewports[document.Id];
            var storedContext = _workspaceContexts["materials:" + document.Id];
            Navigation.SelectedItem = Navigation.MenuItems[1]; await Task.Delay(100);
            Navigation.SelectedItem = Navigation.MenuItems[0]; await Task.Delay(150);
            checks["reading-context-roundtrip"] = _sourcePageIndex == 3 && Math.Abs(SourceViewer.ZoomFactor - storedView.Zoom) < .02
                && ViewModel.SelectedQuestion.Saved?.Id == saved.Id
                && AnswerEditor.SelectionStart == storedContext.SelectionStart && AnswerEditor.SelectionLength == storedContext.SelectionLength;
            Navigation.SelectedItem = Navigation.MenuItems[1];
            Navigation.SelectedItem = Navigation.MenuItems[2];
            Navigation.SelectedItem = Navigation.MenuItems[0]; await Task.Delay(200);
            checks["rapid-navigation-keeps-latest"] = ViewModel.CurrentDocument?.Id == document.Id && ViewModel.SectionTitle == "资料" && ViewModel.Questions.Count == 1;
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
            var teacherDocument = await _library.ImportDocumentAsync("教师批改示例", ImportMode.TeacherGraded, new ImportSource[] { new("教师批改.png", bytes) });
            var teacherRegion = region with { PageId = teacherDocument.Pages[0].Id };
            var teacherGrade = new Grade { Verdict = Verdict.Incorrect, Earned = 0, Possible = 2, Source = GradeSource.Teacher, Reason = "教师判错" };
            var teacherQuestion = question with
            {
                DraftKey = "workflow-teacher",
                Points = 2,
                Sources = [teacherRegion],
                Attempt = new Attempt
                {
                    StudentAnswer = "原作答",
                    StudentRegions = [teacherRegion],
                    AiSolution = "原解题过程",
                    Conflict = "保留判断差异",
                    Grade = teacherGrade,
                    Teacher = new TeacherMark { Raw = "× 0/2", Regions = [teacherRegion], Grade = teacherGrade }
                }
            };
            var teacherJob = job with { Id = Guid.NewGuid().ToString(), State = JobState.Running, Sequence = 1, Input = job.Input! with { Document = teacherDocument } };
            await _library.SaveJobEventAsync(teacherJob, Event(teacherJob, "started")); await _library.BeginGradingAsync(teacherJob);
            var teacherSaved = await _library.SaveGradingQuestionAsync(teacherJob.Id, teacherQuestion);
            teacherJob = teacherJob with { State = JobState.Completed, Sequence = 2 }; await _library.SaveJobEventAsync(teacherJob, Event(teacherJob, "completed"));
            var teacherEditor = new QuestionFields(teacherSaved.Question, teacherDocument);
            SetEditorText(teacherEditor.Panel, "学生作答", "更正后的学生作答");
            SetEditorText(teacherEditor.Panel, "标准答案", "独立的标准答案");
            var teacherEdited = await _library.EditQuestionAsync(teacherSaved.Id, teacherEditor.Read());
            checks["manual-edit-keeps-answer-evidence-separate"] = teacherEdited.Question.StandardAnswer == "独立的标准答案"
                && teacherEdited.Question.Attempt?.StudentAnswer == "更正后的学生作答"
                && teacherEdited.Question.Attempt.Teacher?.Raw == "× 0/2"
                && teacherEdited.Question.Attempt.Teacher.Grade?.Source == GradeSource.Teacher
                && teacherEdited.Question.Attempt.Grade.Source == GradeSource.Manual
                && teacherEdited.Question.Attempt.StudentRegions.Single() == teacherRegion
                && teacherEdited.Question.Attempt.Conflict == "保留判断差异";
            var failedDirectory = Path.Combine(root, "invalid-settings-library");
            await using (var invalidSettingsLibrary = await LibraryRepository.OpenAsync(new(failedDirectory, LibraryOpenMode.CreateNew)))
                await invalidSettingsLibrary.SaveSettingsAsync(AppSettings.Default);
            using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = LegacyLibraryLocation.DatabaseFile(failedDirectory), Mode = SqliteOpenMode.ReadWrite, Pooling = false }.ToString()))
            {
                await connection.OpenAsync();
                using var command = connection.CreateCommand();
                command.CommandText = "UPDATE settings SET settings_json=$json WHERE id=1";
                command.Parameters.AddWithValue("$json", "{invalid-settings"); await command.ExecuteNonQueryAsync();
            }
            string previousPointer = await File.ReadAllTextAsync(_libraryPointerOverride);
            bool switchRejected = false;
            try { await SwitchLibraryAsync(failedDirectory); } catch (IOException) { switchRejected = true; }
            checks["failed-initialization-keeps-active-library"] = switchRejected && _library is not null && _jobs is not null
                && _libraryDirectory == previousPointer && ViewModel.CurrentDocument?.Id == document.Id
                && await File.ReadAllTextAsync(_libraryPointerOverride) == previousPointer;
            await using (var released = await LibraryRepository.OpenAsync(new(failedDirectory, LibraryOpenMode.ReadWrite)))
                checks["failed-initialization-releases-writer"] = released is not null;
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
            exitCode = checks.Where(check => check.Key != "native-aot").All(check => check.Value) ? 0 : 1;
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

    private static bool SetEditorText(FrameworkElement element, string header, string text)
    {
        if (element is TextBox box && box.Header is string label && label == header) { box.Text = text; return true; }
        if (element is Panel panel)
            foreach (var child in panel.Children.OfType<FrameworkElement>()) if (SetEditorText(child, header, text)) return true;
        if (element is Border { Child: FrameworkElement borderChild }) return SetEditorText(borderChild, header, text);
        if (element is ContentControl { Content: FrameworkElement contentChild }) return SetEditorText(contentChild, header, text);
        return false;
    }

    private static Expander? FindEditorSection(FrameworkElement element, string header)
    {
        if (element is Expander expander && expander.Header is string label && label == header) return expander;
        if (element is Panel panel)
            foreach (var child in panel.Children.OfType<FrameworkElement>()) if (FindEditorSection(child, header) is { } found) return found;
        if (element is Border { Child: FrameworkElement borderChild }) return FindEditorSection(borderChild, header);
        if (element is ContentControl { Content: FrameworkElement contentChild }) return FindEditorSection(contentChild, header);
        return null;
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
