using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Jobs;
using OhMyStudy.Infrastructure.Storage;
using System.Text.Json;
using System.Text.Json.Nodes;

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
            _diagnosticRate = new(7.12, "2026-10-20", false);
            var image = await MakeSyntheticPngAsync();
            var document = await _library!.ImportDocumentAsync("数学单元练习", ImportMode.Blank, Enumerable.Range(0, 4).Select(_ => new ImportSource("原件.png", image)).ToArray());
            var runner = new TaskSmokeRunner { Library = _library };
            _jobs = await JobQueue.CreateAsync(new LibraryJobStore(_library!), runner, new TaskSmokeExecutor(), Path.Combine(root, "results"));
            _ = ObserveJobsAsync();
            var policy = new ExecutionPolicy { PreferredMode = ExecutionMode.RequestPermission, EffectiveMode = ExecutionMode.RequestPermission, ExecutorKind = ExecutorKind.Host, SandboxNetworking = false };
            var input = new GradingInput
            {
                Document = document,
                Provider = new() { Id = Guid.NewGuid().ToString(), Name = "示例", BaseUrl = "https://synthetic.invalid", Protocol = Protocol.Responses, Models = [] },
                Model = new() { Id = "synthetic", Capabilities = Capabilities.Unknown, Currency = "USD" },
                AllowUnknown = true
            };
            runner.Mode = "complete";
            var first = await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, input);
            await Wait(first.Id, j => j.State.IsTerminal());
            _selectedTaskId = first.Id; await RefreshTasksAsync();
            Check(TaskQuestions.Text == "3" && _taskSteps.Count == 3 && _taskSteps["read-1"].Title == "阅读页面（4 次）" && TaskProgress.Visibility == Visibility.Collapsed, "completed-metrics-and-grouped-log");
            Check(!_taskSteps.Keys.Any(key => key.StartsWith("model-read-", StringComparison.Ordinal)) && _taskSteps.Values.All(view => !view.Title.Contains("轮", StringComparison.Ordinal)), "cross-turn-tools-merge-without-turn-headings");
            Check(TaskInputCached.Visibility == Visibility.Visible && TaskInputCached.Text == "缓存命中 1,102,400"
                && TaskInputUncached.Text == "缓存未命中 29,454" && TaskInputCacheWrite.Text == "缓存写入 4,096", "cache-buckets-visible");
            await Capture("completed");
            Check(_taskSteps["read-1"].Heading == "阅读页面（4 次）" && !_taskSteps["read-1"].DetailVisible, "successful-tool-log-shows-only-tool-name");
            Check(_taskSteps["model-1"].OutputVisible && FindVisualChild<ScrollViewer>(_taskSteps["model-1"].Root) is null, "model-reply-is-inline-prose-with-one-page-scroll");
            Check(_taskSteps["model-1"].OutputTrailingSpace <= 12, "reply-followed-by-action-has-no-extra-bottom-gap");
            Check(TaskTitle.FontSize == 24 && TaskSubtitle.FontSize == 14 && _taskItems[first.Id].Title.FontSize == 16, "task-page-readable-type-scale");
            TaskUsage_Click(TaskUsageButton, new RoutedEventArgs()); await Capture("usage-expanded");
            Check(TaskUsageFlyout.IsOpen, "usage-details-open-separately");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("usage-expanded-dark"); RootGrid.RequestedTheme = ElementTheme.Light;
            TaskUsageFlyout.Hide();
            Check(FindVisualChild<HyperlinkButton>(TaskLog) is null && !_taskSteps["read-1"].OutputVisible, "tool-log-has-no-output-links-or-raw-output");
            TaskLogScroll.ChangeView(null, 0, null, true);
            var runningDocument = await _library.ImportDocumentAsync("数学 仅试题", ImportMode.Blank, Enumerable.Range(0, 4).Select(_ => new ImportSource("原件.png", image)).ToArray());
            runner.Mode = "running";
            var active = await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, input with { Document = runningDocument });
            await Wait(active.Id, j => j.Sequence >= 12);
            _selectedTaskId = active.Id; await RefreshTasksAsync(); await Capture("running");
            Check(TaskInputCached.Visibility == Visibility.Visible && TaskInputUsage.Text == "输入 1,135,950", "running-usage-visible");
            Check(TaskProgress.Visibility == Visibility.Visible && TaskCancelButton.Visibility == Visibility.Visible, "live-progress-and-cancel");
            await runner.Update!.ActivityAsync(new JobActivity { Key = "model-2", Kind = "model", Title = "第 2 轮识别", Detail = "正在等待模型响应", PagesRead = 4, QuestionsSaved = 3 });
            await RefreshTasksAsync(); await Capture("waiting-no-reply");
            Check(TaskLog.ActualWidth >= TaskLogScroll.ActualWidth - 2 && _taskSteps.Values.All(step => step.Root.ActualWidth >= TaskLog.ActualWidth - 6), "log-uses-available-width");
            Check(_taskSteps["read-1"].Root.ActualHeight < 45 && FindVisualChild<Border>(_taskSteps["read-1"].Root) is null, "compact-grouped-action-log");
            var retainedGroup = _taskSteps["read-1"].Root;
            await runner.Update!.ActivityAsync(new JobActivity { Key = "read-2", Kind = "read_page", Title = "阅读第 2 页", Detail = "页面暂时无法读取，请重试。", State = "failed", PagesRead = 2, ElapsedMs = 120 });
            await Capture("grouped-error");
            Check(_taskSteps["read-2"].Heading == "阅读第 2 页  失败" && _taskSteps["read-2"].DetailVisible, "failed-tool-log-shows-failure-and-reason");
            Check(_taskSteps.ContainsKey("read-2") && _taskSteps["read-3"].Title == "阅读页面（2 次）", "failed-tool-kept-separate-in-order");
            await runner.Update!.ActivityAsync(new JobActivity { Key = "read-2", Kind = "read_page", Title = "阅读第 2 页", Detail = "已读取原件", State = "completed", PagesRead = 2, ElapsedMs = 120 });
            await Capture("regrouped");
            Check(_taskSteps.Count == 4 && !_taskSteps.ContainsKey("read-2") && ReferenceEquals(retainedGroup, _taskSteps["read-1"].Root), "successful-tools-regroup-without-recreating-log");
            await runner.Update!.ActivityAsync(new JobActivity { Key = "model-2", Kind = "model", Title = "第 2 轮识别", Detail = "正在核对题目", Text = "这是一段简短的模型回复。\n\n\n\n", PagesRead = 4, QuestionsSaved = 3 });
            await RefreshTasksAsync(); await Capture("short-reply");
            Check(_taskSteps["model-2"].OutputFitsContent && !_taskSteps["model-2"].OutputHasTrailingBlankLines, "reply-shrinks-to-content-and-trims-blank-lines");
            Check(FindVisualChild<Expander>(_taskSteps["model-2"].Root) is null, "reply-has-no-nested-card");
            var retained = _taskSteps["model-2"].Root;
            await runner.Update!.ActivityAsync(new JobActivity { Key = "model-2", Kind = "model", Title = "第 2 轮识别", Detail = "正在接收模型响应", Text = string.Join("\n", Enumerable.Repeat("正在核对第 4 页的跨页题目，已保存的题目可以在题库查看。", 20)), PagesRead = 4, QuestionsSaved = 3, ElapsedMs = 8250 });
            await RefreshTasksAsync(); Check(ReferenceEquals(retained, _taskSteps["model-2"].Root), "streaming-preserves-controls");
            await Capture("long-output");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("dark");
            RootGrid.RequestedTheme = ElementTheme.Light;
            Check(ActionVisible(TaskCancelButton), "cancel-action-visible");
            Check(LatestHeadingVisible(), "latest-record-visible");
            TaskLogScroll.ChangeView(null, 0, null, true); await Task.Delay(150);
            Check(!_taskFollowLatest, "reading-history-pauses-follow");
            await runner.Update!.ActivityAsync(new JobActivity { Key = "model-2", Kind = "model", Title = "第 2 轮识别", Detail = "正在核对题目", Text = string.Join("\n", Enumerable.Repeat("正在核对第 4 页的跨页题目，已保存的题目可以在题库查看。", 22)), PagesRead = 4, QuestionsSaved = 3, ElapsedMs = 9000 });
            await Capture("reading-history");
            Check(TaskLogScroll.VerticalOffset < 1 && !_taskFollowLatest, "streaming-keeps-history-reading-position");
            TaskLatest_Click(TaskLogScroll, new RoutedEventArgs()); await Capture("latest");
            Check(LatestHeadingVisible(), "latest-action-resumes-follow");
            await _jobs.CancelAsync(active.Id); await RefreshTasksAsync(); await Capture("cancelled");
            Check(TaskCancelButton.Visibility == Visibility.Collapsed && TaskStateLabel.Text == "已取消", "cancelled-state");
            runner.Mode = "error";
            var failed = await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, input with { Document = input.Document with { Id = Guid.NewGuid().ToString(), Name = "这是一份文件名称很长的数学试卷，包含跨页题目、图表与详细解答，需要保留完整名称以便查找.pdf" } });
            await Wait(failed.Id, j => j.State.IsTerminal()); _selectedTaskId = failed.Id;
            await RefreshTasksAsync(); await Capture("failed");
            runner.Mode = "permission";
            var permission = await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, input with { Document = input.Document with { Id = Guid.NewGuid().ToString(), Name = "数学辅助计算" } });
            await Wait(permission.Id, j => j.State == JobState.WaitingPermission); _selectedTaskId = permission.Id;
            await RefreshTasksAsync(); await Capture("permission");
            Check(TaskPermissionButton.Visibility == Visibility.Visible && TaskProgress.Visibility == Visibility.Collapsed, "permission-state");
            Check(ActionVisible(TaskPermissionButton) && ActionVisible(TaskCancelButton), "permission-actions-visible");
            Check(LatestHeadingVisible(), "pending-record-visible");
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
            _selectedTaskId = first.Id; await RefreshTasksAsync(); TaskLogScroll.ChangeView(null, 0, null, true); await Capture("final");
            runner.Mode = "unpriced";
            var unpriced = await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, input);
            await Wait(unpriced.Id, j => j.State.IsTerminal()); _selectedTaskId = unpriced.Id; await RefreshTasksAsync();
            Check(TaskUsage.Text == "—" && TaskRateNote.Text == "价格或用量不完整，暂无法估算费用。", "incomplete-prices-do-not-show-partial-total");
            TaskUsage_Click(TaskUsageButton, new RoutedEventArgs()); await Capture("usage-unknown");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("usage-unknown-dark"); RootGrid.RequestedTheme = ElementTheme.Light; TaskUsageFlyout.Hide();
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
            if (TaskLog.Children.LastOrDefault() is not FrameworkElement latest) return false;
            double top = latest.TransformToVisual(TaskLogScroll).TransformPoint(new(0, 0)).Y;
            return top >= -1 && top + 32 <= TaskLogScroll.ActualHeight;
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
        public LibraryRepository? Library { get; init; }
        public JobContext? Update { get; private set; }
        public async Task RunAsync(Job job, JobContext context)
        {
            string mode = Mode; Update = context;
            if (mode is "complete" or "running" or "unpriced" && Library is not null)
            {
                await Library.BeginGradingAsync(job);
                await Library.RecordModelCallAsync(job.Id, 1, ModelCall(668420, 21880, 655360, mode == "unpriced" ? null : 3.41), CancellationToken.None);
                await Library.RecordModelCallAsync(job.Id, 2, ModelCall(467530, 27749, 447040, 4.59), CancellationToken.None);
            }
            await context.ActivityAsync(new JobActivity { Key = "model-1", Kind = "model", Title = "第 1 轮识别", Detail = "已生成 4 项操作", Text = "已找到 4 页试题。我将先读取原件，再保存识别出的题目，并核对跨页内容。", State = "completed", ElapsedMs = 2300 });
            for (int i = 1; i <= 4; i++)
            {
                if (i > 1) await context.ActivityAsync(new JobActivity { Key = "model-read-" + i, Kind = "model", Title = $"第 {i} 轮识别", Detail = "已生成 1 项操作", Text = i == 3 ? "  \n\n" : "", State = "completed", ElapsedMs = 1200 });
                await context.ActivityAsync(new JobActivity { Key = "read-" + i, Kind = "read_page", Title = $"阅读第 {i} 页", Detail = "已读取原件", Text = i == 1 ? "原件已读取，可以继续识别题目。" : "", State = "completed", PagesRead = i, ElapsedMs = 120 });
            }
            await context.ActivityAsync(new JobActivity { Key = "save-1", Kind = "save_question", Title = "保存题目", Detail = "3 道题目已加入题库", State = "completed", PagesRead = 4, QuestionsSaved = 3, ElapsedMs = 80 });
            if (mode is "complete" or "unpriced") return;
            await context.ActivityAsync(new JobActivity { Key = "model-2", Kind = "model", Title = "第 2 轮识别", Detail = "正在接收模型响应", Text = "正在核对第 4 页的跨页题目。", PagesRead = 4, QuestionsSaved = 3, ElapsedMs = 3200 });
            if (mode == "error")
            {
                await context.ActivityAsync(new JobActivity { Key = "model-2", Kind = "model", Title = "识别", State = "failed", Detail = "模型连接中断或超时，请检查网络后重试。", Text = "正在核对第 4 页的跨页题目。", PagesRead = 4, QuestionsSaved = 3 });
                throw new IOException("Synthetic disconnected stream");
            }
            if (mode == "permission")
            {
                await context.ActivityAsync(new JobActivity { Key = "compute", Kind = "python", Title = "运行辅助计算", Detail = "等待你确认执行请求", PagesRead = 4, QuestionsSaved = 3 });
                await context.ExecuteAsync(new ExecutionCall { ProtocolVersion = 1, JobId = job.Id, CallId = "sample-compute", Tool = ToolKind.Python, Content = "print(1 + 1)", Arguments = [], WorkingDirectory = Path.GetTempPath(), TimeoutMs = 60000 });
            }
            await Task.Delay(Timeout.Infinite, context.Cancellation);
        }
        private static JsonElement ModelCall(ulong input, ulong output, ulong cacheRead, double? cost) =>
            JsonDocument.Parse(new JsonObject
            {
                ["model"] = "example-model",
                ["usage"] = new JsonObject { ["input"] = input, ["input_uncached"] = input - cacheRead - 2048, ["output"] = output, ["cache_read"] = cacheRead, ["cache_write"] = 2048 },
                ["estimated_cost"] = cost,
                ["currency"] = "USD"
            }.ToJsonString()).RootElement.Clone();
    }
}
