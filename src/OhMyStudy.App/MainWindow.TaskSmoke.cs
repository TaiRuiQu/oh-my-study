using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Jobs;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    // Isolated UI fixtures only. No credentials, user library, provider, or executable tool is used.
    private async Task RunTaskSmokeAsync(string reportPath)
    {
        string root = Path.Combine(Path.GetDirectoryName(reportPath)!, "tasks-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); _libraryPointerOverride = Path.Combine(root, "active-library.txt");
        var checks = new List<string>(); int exit = 1;
        try
        {
            await InitializeLibraryAsync(Path.Combine(root, "library"));
            Navigation.SelectedItem = Navigation.MenuItems[3]; await Task.Delay(150); await RefreshTasksAsync();
            TaskSampleMark.Visibility = Visibility.Visible;
            await Capture("empty");
            Check(TaskEmpty.Visibility == Visibility.Visible, "empty-state");
            await _jobs!.DisposeAsync();
            var runner = new TaskSmokeRunner();
            _jobs = await JobQueue.CreateAsync(new LibraryJobStore(_library!), runner, new TaskSmokeExecutor(), Path.Combine(root, "results"));
            _ = ObserveJobsAsync();
            var policy = new ExecutionPolicy { PreferredMode = ExecutionMode.RequestPermission, EffectiveMode = ExecutionMode.RequestPermission, ExecutorKind = ExecutorKind.Host, SandboxNetworking = false };
            var input = new GradingInput
            {
                Document = new() { Id = Guid.NewGuid().ToString(), Name = "数学单元练习", Mode = ImportMode.Blank, CreatedAt = DateTimeOffset.UtcNow.ToString("O"), Pages = [], PendingPdfPages = false },
                Provider = new() { Id = Guid.NewGuid().ToString(), Name = "示例", BaseUrl = "https://synthetic.invalid", Protocol = Protocol.Responses, Models = [] },
                Model = new() { Id = "synthetic", Capabilities = Capabilities.Unknown, Currency = "USD" },
                AllowUnknown = true
            };
            for (uint i = 0; i < 4; i++) input.Document.Pages.Add(new SourcePage { Id = Guid.NewGuid().ToString(), AssetId = Guid.NewGuid().ToString(), PageIndex = i, Rotation = 0, Role = "material", PreviewStatus = "ready" });
            runner.Mode = "complete";
            var first = await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, input);
            await Wait(first.Id, j => j.State.IsTerminal());
            _selectedTaskId = first.Id; await RefreshTasksAsync();
            Check(TaskQuestions.Text == "3" && _taskSteps.Count == 6 && TaskProgress.Visibility == Visibility.Collapsed, "completed-metrics-and-timeline");
            await Capture("completed");
            _taskSteps["model-1"].Expand(); TaskTimelineScroll.ChangeView(null, 0, null, true); await Capture("expanded");
            runner.Mode = "running";
            var active = await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, input with { Document = input.Document with { Id = Guid.NewGuid().ToString(), Name = "数学 仅试题" } });
            await Wait(active.Id, j => j.Sequence >= 8);
            _selectedTaskId = active.Id; await RefreshTasksAsync(); await Capture("running");
            Check(TaskProgress.Visibility == Visibility.Visible && TaskCancelButton.Visibility == Visibility.Visible, "live-progress-and-cancel");
            var retained = _taskSteps["model-2"].Root;
            await runner.Update!.ActivityAsync(new JobActivity { Key = "model-2", Kind = "model", Title = "第 2 轮识别", Detail = "正在接收模型响应", Text = string.Join("\n", Enumerable.Repeat("正在核对第 4 页的跨页题目，已保存的题目可以在题库查看。", 12)), PagesRead = 4, QuestionsSaved = 3, ElapsedMs = 8250 });
            await RefreshTasksAsync(); Check(ReferenceEquals(retained, _taskSteps["model-2"].Root), "streaming-preserves-controls");
            await Capture("long-output");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("dark");
            RootGrid.RequestedTheme = ElementTheme.Light;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 850)); await Capture("medium");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 650)); await Capture("short");
            Check(ActionVisible(TaskCancelButton), "short-window-cancel-visible");
            Check(LatestHeadingVisible(), "short-window-latest-step-visible");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 960)); await Capture("narrow");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("compact");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("dark-compact"); RootGrid.RequestedTheme = ElementTheme.Light;
            Check(Grid.GetRow(TaskDetail) == 1 && TaskDetail.ActualWidth <= TasksPanel.ActualWidth + 1 && TaskCompactPicker.Visibility == Visibility.Visible && ActionVisible(TaskCancelButton), "compact-layout");
            Check(LatestHeadingVisible(), "compact-latest-step-visible");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            await _jobs.CancelAsync(active.Id); await RefreshTasksAsync(); await Capture("cancelled");
            Check(TaskCancelButton.Visibility == Visibility.Collapsed && TaskStateLabel.Text == "已取消", "cancelled-state");
            runner.Mode = "error";
            var failed = await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, input with { Document = input.Document with { Id = Guid.NewGuid().ToString(), Name = "这是一份文件名称很长的数学试卷，包含跨页题目、图表与详细解答，需要在窄窗口中完整显示.pdf" } });
            await Wait(failed.Id, j => j.State.IsTerminal()); _selectedTaskId = failed.Id;
            await RefreshTasksAsync(); await Capture("failed");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("failed-compact");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            runner.Mode = "permission";
            var permission = await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, input with { Document = input.Document with { Id = Guid.NewGuid().ToString(), Name = "数学辅助计算" } });
            await Wait(permission.Id, j => j.State == JobState.WaitingPermission); _selectedTaskId = permission.Id;
            await RefreshTasksAsync(); await Capture("permission");
            Check(TaskPermissionButton.Visibility == Visibility.Visible && TaskProgress.Visibility == Visibility.Collapsed, "permission-state");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("permission-compact");
            Check(ActionVisible(TaskPermissionButton) && ActionVisible(TaskCancelButton), "compact-permission-actions-visible");
            Check(LatestHeadingVisible(), "compact-pending-step-visible");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            var review = ReviewPermissionAsync(permission.Id, (await _jobs.GetAsync(permission.Id)).PendingPermission!);
            await Task.Delay(250);
            var dialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().Single();
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".permission-dialog.png"), dialog); dialog.Hide(); await review;
            RootGrid.RequestedTheme = ElementTheme.Dark;
            var darkReview = ReviewPermissionAsync(permission.Id, (await _jobs.GetAsync(permission.Id)).PendingPermission!);
            await Task.Delay(250);
            var darkDialog = VisualTreeHelper.GetOpenPopupsForXamlRoot(RootGrid.XamlRoot).Select(p => p.Child).OfType<ContentDialog>().Single();
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, ".permission-dialog-dark.png"), darkDialog); darkDialog.Hide(); await darkReview;
            RootGrid.RequestedTheme = ElementTheme.Light;
            await _jobs.CancelAsync(permission.Id);
            await _jobs.DisposeAsync();
            _jobs = await JobQueue.CreateAsync(new LibraryJobStore(_library!), runner, new TaskSmokeExecutor(), Path.Combine(root, "results"));
            await RefreshTasksAsync();
            Check(_taskSteps.Count > 0 && (await _library!.ListJobEventsAsync(first.Id)).Any(e => e.Kind == "activity"), "durable-history-reopened");
            Check(RootGrid.KeyboardAcceleratorPlacementMode == Microsoft.UI.Xaml.Input.KeyboardAcceleratorPlacementMode.Hidden && ToolTipService.GetToolTip(RootGrid) is null, "global-shortcut-tooltip-disabled");
            _selectedTaskId = first.Id; await RefreshTasksAsync(); TaskTimelineScroll.ChangeView(null, 0, null, true); await Capture("final");
            exit = 0;
            if (Environment.GetCommandLineArgs().Contains("--inspect-ui", StringComparer.Ordinal))
            { await File.WriteAllLinesAsync(reportPath, checks.Prepend("PASS")); AppWindow.Show(); Activate(); await Task.Delay(TimeSpan.FromMinutes(10)); }
        }
        catch (Exception error) { AppLog.Error("TaskSmoke", error); checks.Add("FAIL " + error); }
        finally
        {
            await File.WriteAllLinesAsync(reportPath, checks.Prepend(exit == 0 ? "PASS" : "FAIL"));
            if (_jobs is not null) await _jobs.DisposeAsync(); _jobs = null;
            if (_library is not null) await _library.DisposeAsync(); _library = null;
            _shutdownFinished = true; Microsoft.UI.Xaml.Application.Current.Exit(); Environment.Exit(exit);
        }
        void Check(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        bool ActionVisible(FrameworkElement action)
        {
            double y = action.TransformToVisual(TaskDetail).TransformPoint(new(0, 0)).Y;
            return y >= 0 && y + action.ActualHeight <= TaskDetail.ActualHeight;
        }
        bool LatestHeadingVisible()
        {
            if (TaskTimeline.Children.LastOrDefault() is not FrameworkElement latest) return false;
            double top = latest.TransformToVisual(TaskTimelineScroll).TransformPoint(new(0, 0)).Y;
            return top >= -1 && top + 32 <= TaskTimelineScroll.ActualHeight;
        }
        async Task Capture(string name) { await RefreshTasksAsync(); await Task.Delay(650); RootGrid.UpdateLayout(); await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + ".png")); }
        async Task Wait(string id, Func<Job, bool> predicate)
        {
            for (int attempt = 0; attempt < 200; attempt++) { if (predicate(await _jobs!.GetAsync(id))) return; await Task.Delay(25); }
            throw new TimeoutException("Synthetic task did not reach expected state.");
        }
    }

    private sealed class TaskSmokeExecutor : IToolExecutor
    {
        public Task<ToolExecutionResult> ExecuteAsync(OhMyStudy.Application.Execution.AuthorizedCall call, string resultsDirectory, CancellationToken cancellationToken)
            => throw new InvalidOperationException("UI fixtures must never execute tools.");
    }
    private sealed class TaskSmokeRunner : IJobRunner
    {
        public string Mode { get; set; } = "complete";
        public JobContext? Update { get; private set; }
        public async Task RunAsync(Job job, JobContext context)
        {
            string mode = Mode; Update = context;
            await context.ActivityAsync(new JobActivity { Key = "model-1", Kind = "model", Title = "第 1 轮识别", Detail = "已生成 4 项操作", Text = "已找到 4 页试题。我将先读取原件，再保存识别出的题目，并核对跨页内容。", State = "completed", ElapsedMs = 2300 });
            for (int i = 1; i <= 4; i++)
                await context.ActivityAsync(new JobActivity { Key = "read-" + i, Kind = "read_page", Title = $"阅读第 {i} 页", Detail = "已读取原件", State = "completed", PagesRead = i, ElapsedMs = 120 });
            await context.ActivityAsync(new JobActivity { Key = "save-1", Kind = "save_question", Title = "保存题目", Detail = "3 道题目已加入题库", State = "completed", PagesRead = 4, QuestionsSaved = 3, ElapsedMs = 80 });
            if (mode == "complete") return;
            await context.ActivityAsync(new JobActivity { Key = "model-2", Kind = "model", Title = "第 2 轮识别", Detail = "正在接收模型响应", Text = "正在核对第 4 页的跨页题目。", PagesRead = 4, QuestionsSaved = 3, ElapsedMs = 3200 });
            if (mode == "error") throw new IOException("连接中断，已保存的题目会保留。请稍后重新识别。");
            if (mode == "permission")
            {
                await context.ActivityAsync(new JobActivity { Key = "compute", Kind = "python", Title = "运行辅助计算", Detail = "等待你确认执行请求", PagesRead = 4, QuestionsSaved = 3 });
                await context.ExecuteAsync(new ExecutionCall { ProtocolVersion = 1, JobId = job.Id, CallId = "sample-compute", Tool = ToolKind.Python, Content = "print(1 + 1)", Arguments = [], WorkingDirectory = Path.GetTempPath(), TimeoutMs = 60000 });
            }
            await Task.Delay(Timeout.Infinite, context.Cancellation);
        }
    }
}
