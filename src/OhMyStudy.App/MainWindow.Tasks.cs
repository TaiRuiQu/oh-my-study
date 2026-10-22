using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private string? _selectedTaskId;
    private Job? _selectedTask;
    private bool _updatingTaskPicker;
    private readonly Dictionary<string, (ListViewItem Item, TextBlock Title, TextBlock Status, FontIcon StatusIcon, TextBlock Time, ComboBoxItem Compact, TextBlock CompactTitle)> _taskItems = [];
    private readonly Dictionary<string, TaskLogEntryView> _taskSteps = [];
    private string? _logJobId;
    private ulong _renderedTaskSequence;
    private bool _taskFollowLatest;
    private double _taskScrollHeight;
    private double _taskScrollWidth;
    private double _taskViewportHeight;
    private bool _taskScrollScheduled;

    private void TasksPanel_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyTaskLayout();
    private void ApplyTaskLayout()
    {
        if (TaskListColumn is null) return;
        bool stacked = TasksPanel.ActualWidth < 850;
        TaskListColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(268);
        TasksPanel.ColumnDefinitions[1].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        TaskListRow.Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        TasksPanel.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(TaskDetail, stacked ? 0 : 1); Grid.SetRow(TaskDetail, stacked ? 1 : 0);
        TaskSidebar.Visibility = !stacked && TaskDetail.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        TaskCompactPicker.Visibility = stacked && TaskDetail.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        TaskSummaryScroll.MaxHeight = Math.Max(120, (TasksPanel.ActualHeight - (stacked ? 46 : 0)) * .54);
        WorkspaceStatus.Visibility = TasksPanel.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void TaskPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTaskPicker || TaskPicker.SelectedItem is not ListViewItem { Tag: string id }) return;
        _selectedTaskId = id;
        try { await RefreshTasksAsync(); }
        catch (Exception error) { AppLog.Error("Task.Select", error); }
    }
    private async void TaskCompactPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_updatingTaskPicker || TaskCompactPicker.SelectedItem is not ComboBoxItem { Tag: string id }) return;
        _selectedTaskId = id;
        try { await RefreshTasksAsync(); }
        catch (Exception error) { AppLog.Error("Task.Select", error); }
    }

    private async Task RefreshTasksAsync()
    {
        if (_jobs is null || _closed) return;
        long generation = ++_taskRefreshGeneration;
        var jobs = await _jobs.ListAsync(_lifetime.Token);
        if (_closed || generation != _taskRefreshGeneration) return;
        var active = jobs.FirstOrDefault(j => !j.State.IsTerminal());
        _activeJob = active?.Id; ViewModel.UpdateJob(active);
        if (TasksPanel.Visibility != Visibility.Visible) return;
        bool empty = jobs.Count == 0;
        TaskEmpty.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        TaskSidebar.Visibility = TaskDetail.Visibility = empty ? Visibility.Collapsed : Visibility.Visible;
        if (empty) { _selectedTaskId = null; _selectedTask = null; TaskCompactPicker.Visibility = Visibility.Collapsed; return; }
        _updatingTaskPicker = true;
        try
        {
            var ids = jobs.Select(j => j.Id).ToHashSet(StringComparer.Ordinal);
            foreach (string stale in _taskItems.Keys.Where(id => !ids.Contains(id)).ToArray())
            { TaskPicker.Items.Remove(_taskItems[stale].Item); TaskCompactPicker.Items.Remove(_taskItems[stale].Compact); _taskItems.Remove(stale); }
            foreach (var job in jobs.OrderByDescending(j => j.CreatedAt))
            {
                if (!_taskItems.TryGetValue(job.Id, out var row))
                {
                    var title = new TextBlock { TextWrapping = TextWrapping.Wrap, MaxLines = 2, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                    var status = new TextBlock { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["QuietTextStyle"], TextWrapping = TextWrapping.Wrap };
                    var content = new StackPanel { Spacing = 6, Padding = new Thickness(4, 10, 4, 10) };
                    var statusIcon = new FontIcon { FontSize = 12 }; var time = new TextBlock { Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["QuietTextStyle"] };
                    var metadata = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                    metadata.Children.Add(statusIcon); metadata.Children.Add(status);
                    metadata.Children.Add(new FontIcon { Glyph = "\uE787", FontSize = 12, Margin = new Thickness(6, 0, 0, 0) }); metadata.Children.Add(time);
                    content.Children.Add(title); content.Children.Add(metadata);
                    var item = new ListViewItem { Content = content, Tag = job.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    var compactTitle = new TextBlock { TextTrimming = TextTrimming.CharacterEllipsis };
                    var compact = new ComboBoxItem { Content = compactTitle, Tag = job.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    row = (item, title, status, statusIcon, time, compact, compactTitle); _taskItems.Add(job.Id, row);
                }
                row.Title.Text = job.Input?.Document.Name ?? "资料整理";
                row.Status.Text = TaskState(job.State);
                row.StatusIcon.Glyph = TaskStateGlyph(job.State);
                row.StatusIcon.Foreground = TaskBrush(job.State is JobState.Failed or JobState.Interrupted ? "TaskErrorBrush" : "TaskAccentBrush");
                row.Time.Text = $"{DateTimeOffset.FromUnixTimeMilliseconds((long)job.CreatedAt).ToLocalTime():MM/dd HH:mm}";
                row.CompactTitle.Text = row.Title.Text + "（" + TaskState(job.State) + "）";
                ToolTipService.SetToolTip(row.Compact, row.CompactTitle.Text);
                AutomationProperties.SetName(row.Item, row.Title.Text + "，" + TaskState(job.State));
            }
            var ordered = jobs.OrderByDescending(j => j.CreatedAt).Select(j => _taskItems[j.Id].Item).ToArray();
            // Preserve the item identities and selection while updating order.
            for (int i = 0; i < ordered.Length; i++)
                if (i >= TaskPicker.Items.Count || !ReferenceEquals(TaskPicker.Items[i], ordered[i]))
                { TaskPicker.Items.Remove(ordered[i]); TaskPicker.Items.Insert(i, ordered[i]); }
            var compactOrder = jobs.OrderByDescending(j => j.CreatedAt).Select(j => _taskItems[j.Id].Compact).ToArray();
            for (int i = 0; i < compactOrder.Length; i++)
                if (i >= TaskCompactPicker.Items.Count || !ReferenceEquals(TaskCompactPicker.Items[i], compactOrder[i]))
                { TaskCompactPicker.Items.Remove(compactOrder[i]); TaskCompactPicker.Items.Insert(i, compactOrder[i]); }
            if (_selectedTaskId is null || !ids.Contains(_selectedTaskId)) _selectedTaskId = active?.Id ?? ordered[0].Tag as string;
            TaskPicker.SelectedItem = _taskItems[_selectedTaskId!].Item;
            TaskCompactPicker.SelectedItem = _taskItems[_selectedTaskId!].Compact;
        }
        finally { _updatingTaskPicker = false; }
        var selected = jobs.First(j => j.Id == _selectedTaskId);
        if (_library is null || _logJobId == selected.Id && _renderedTaskSequence == selected.Sequence) return;
        var events = await _library.ListJobEventsAsync(selected.Id, _lifetime.Token);
        var calls = await _library.ListJobModelCallsAsync(selected.Id, _lifetime.Token);
        if (_closed || generation != _taskRefreshGeneration || selected.Id != _selectedTaskId) return;
        _selectedTask = selected;
        bool changed = _logJobId != selected.Id;
        bool follow = changed ? !selected.State.IsTerminal() : _taskFollowLatest;
        if (changed) _taskFollowLatest = follow;
        if (changed) { _taskSteps.Clear(); TaskLog.Children.Clear(); TaskLogScroll.ChangeView(null, 0, null, true); }
        _logJobId = selected.Id; _renderedTaskSequence = selected.Sequence;
        var activities = new Dictionary<string, (JobActivity Activity, ulong Time)>();
        foreach (var item in events)
        {
            if (item.Kind == "activity")
            {
                var step = DomainJson.Deserialize<JobActivity>(item.Payload.GetRawText());
                if (activities.TryGetValue(step.Key, out var prior)) activities[step.Key] = (step, prior.Time);
                else activities.Add(step.Key, (step, item.CreatedAt));
            }
        }
        // Older jobs truthfully show the state transitions they actually retained.
        if (activities.Count == 0)
            foreach (var item in events.Where(e => e.Kind is "queued" or "started" or "completed" or "failed" or "cancelled" or "interrupted" or "permission_requested"))
            {
                var snapshot = DomainJson.Deserialize<Job>(item.Payload.GetRawText());
                activities[item.Id] = (new JobActivity { Key = item.Id, Kind = "state", Title = TaskState(snapshot.State), Detail = snapshot.Message, State = snapshot.State.IsTerminal() ? "completed" : "running" }, item.CreatedAt);
            }
        if (activities.Count == 0)
            activities["queued"] = (new JobActivity { Key = "queued", Kind = "state", Title = TaskState(selected.State), Detail = selected.Message }, selected.CreatedAt);
        int pages = activities.Values.Max(v => v.Activity.PagesRead);
        int questions = activities.Values.Max(v => v.Activity.QuestionsSaved);
        bool hasTrace = events.Any(e => e.Kind == "activity");
        TaskTitle.Text = selected.Input?.Document.Name ?? "资料整理";
        TaskSubtitle.Text = selected.State switch
        {
            JobState.Completed => "识别完成，题目已加入题库。",
            JobState.Cancelled => "任务已取消，已保存的题目会保留。",
            JobState.Failed or JobState.Interrupted => "识别未完成，已保存的题目会保留。可在试卷中重新识别。",
            JobState.WaitingPermission => "需要你确认一项操作，确认后继续。",
            _ => activities.Values.Last().Activity.Detail
        };
        TaskPages.Text = hasTrace ? $"{pages} / {selected.Input?.Document.Pages.Count ?? pages}" : "—";
        TaskQuestions.Text = hasTrace ? questions.ToString() : "—";
        TaskModelCalls.Text = $"模型调用 {Math.Max(calls.Count, activities.Values.Count(v => v.Activity.Kind == "model"))} 次";
        TaskStateLabel.Text = TaskState(selected.State);
        TaskStateLabel.Foreground = TaskBrush(selected.State is JobState.Failed or JobState.Interrupted ? "TaskErrorBrush" : "TaskAccentBrush");
        TaskHeroIcon.Glyph = selected.State switch { JobState.Completed => "\uE73E", JobState.Failed or JobState.Interrupted => "\uE7BA", JobState.Cancelled => "\uE711", _ => "\uE9F9" };
        TaskHeroIcon.Foreground = TaskStateLabel.Foreground;
        TaskProgress.Visibility = selected.State == JobState.Running ? Visibility.Visible : Visibility.Collapsed;
        TaskCancelButton.Visibility = selected.State.IsTerminal() ? Visibility.Collapsed : Visibility.Visible;
        TaskPermissionButton.Visibility = selected.PendingPermission is null ? Visibility.Collapsed : Visibility.Visible;
        ulong inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, uncachedTokens = 0, cacheWriteTokens = 0;
        bool inputKnown = true, outputKnown = true, cacheKnown = true, uncachedKnown = true, cacheWriteKnown = true;
        double cost = 0; bool priced = calls.Count > 0;
        foreach (var call in calls)
        {
            if (call.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number && a.TryGetUInt64(out var ai)) inputTokens += ai; else inputKnown = false;
                if (usage.TryGetProperty("output", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number && b.TryGetUInt64(out var bi)) outputTokens += bi; else outputKnown = false;
                if (usage.TryGetProperty("cache_read", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number && r.TryGetUInt64(out var ri)) cacheReadTokens += ri; else cacheKnown = false;
                if (usage.TryGetProperty("input_uncached", out var u) && u.ValueKind == System.Text.Json.JsonValueKind.Number && u.TryGetUInt64(out var ui)) uncachedTokens += ui; else uncachedKnown = false;
                if (usage.TryGetProperty("cache_write", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Number && w.TryGetUInt64(out var wi)) cacheWriteTokens += wi; else cacheWriteKnown = false;
            }
            if (!call.TryGetProperty("usage", out _)) { inputKnown = false; outputKnown = false; cacheKnown = false; uncachedKnown = false; cacheWriteKnown = false; }
            if (call.TryGetProperty("estimated_cost", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number && c.TryGetDouble(out var amount)) cost += amount; else priced = false;
        }
        TaskInputUsage.Text = inputKnown ? $"输入 {inputTokens:N0}" : "输入未提供"; TaskOutputUsage.Text = outputKnown ? $"输出 {outputTokens:N0}" : "输出未提供";
        TaskInputUncached.Text = uncachedKnown ? $"缓存未命中 {uncachedTokens:N0}" : "缓存未命中用量未提供";
        TaskInputCached.Text = cacheKnown ? $"缓存命中 {cacheReadTokens:N0}" : "缓存命中用量未提供";
        TaskInputCacheWrite.Text = cacheWriteKnown ? $"缓存写入 {cacheWriteTokens:N0}" : "缓存写入用量未提供";
        TaskNoUsage.Visibility = calls.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskTokenUsage.Visibility = calls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskUsage.Text = "—";
        TaskRateNote.Text = priced ? "正在计算费用…" : calls.Count > 0 ? "价格或用量不完整，暂无法估算费用。" : "暂无费用记录";
        if (priced) _ = UpdateTaskCnyCostAsync(selected.Id, generation, cost, selected.Input?.Model.Currency ?? "USD");
        var effectiveActivities = activities.Values.Select(item =>
        {
            var (activity, time) = item;
            string state = activity.State;
            if (state == "running" && selected.State == JobState.WaitingPermission && activity.Key == activities.Values.Last().Activity.Key) state = "waiting";
            if (state == "running" && selected.State.IsTerminal()) state = selected.State == JobState.Completed ? "completed" : selected.State == JobState.Cancelled ? "cancelled" : "failed";
            return (Activity: activity with { State = state }, Time: time);
        }).ToArray();
        var entries = GroupTaskLogActivities(effectiveActivities);
        var entryIds = entries.Select(entry => entry.Activity.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string stale in _taskSteps.Keys.Where(key => !entryIds.Contains(key)).ToArray())
        { TaskLog.Children.Remove(_taskSteps[stale].Root); _taskSteps.Remove(stale); }
        for (int i = 0; i < entries.Count; i++)
        {
            var (activity, time, tooltip) = entries[i];
            if (!_taskSteps.TryGetValue(activity.Key, out var view))
            { view = new TaskLogEntryView(); _taskSteps.Add(activity.Key, view); }
            if (i >= TaskLog.Children.Count || !ReferenceEquals(TaskLog.Children[i], view.Root))
            { TaskLog.Children.Remove(view.Root); TaskLog.Children.Insert(i, view.Root); }
            view.Update(activity, activity.State, time, TaskBrush, tooltip);
        }
        if (follow) { _taskFollowLatest = true; ScrollToLatestTaskStep(); }
        ApplyTaskLayout();
    }

    private static List<(JobActivity Activity, ulong Time, string? Tooltip)> GroupTaskLogActivities(IReadOnlyList<(JobActivity Activity, ulong Time)> activities)
    {
        // Empty successful model turns are bookkeeping, not readable content. They
        // must not split a run of tools, even when the calls came from many turns.
        activities = activities.Where(item => item.Activity.Kind != "model" || item.Activity.State != "completed" || !string.IsNullOrWhiteSpace(item.Activity.Text)).ToArray();
        var entries = new List<(JobActivity Activity, ulong Time, string? Tooltip)>();
        for (int start = 0; start < activities.Count;)
        {
            var (first, time) = activities[start];
            int end = start + 1;
            // Meaningful replies, pending requests and failures retain their place.
            if (first.State == "completed" && first.Kind is not ("model" or "state"))
                while (end < activities.Count && activities[end].Activity.Kind == first.Kind && activities[end].Activity.State == "completed") end++;
            int count = end - start;
            if (count == 1) entries.Add((first, time, null));
            else
            {
                var members = activities.Skip(start).Take(count).Select(item => item.Activity).ToArray();
                string title = first.Kind switch
                {
                    "read_page" => "阅读页面",
                    "save_question" => "保存题目",
                    "save_references" => "保存参考答案",
                    "python" or "shell" => "运行辅助计算",
                    _ => first.Title
                };
                entries.Add((first with { Title = $"{title}（{count} 次）", Detail = "", Text = "", ElapsedMs = members.Sum(item => item.ElapsedMs) }, time,
                    string.Join("\n", members.Select(item => item.Title + (string.IsNullOrWhiteSpace(item.Detail) ? "" : "：" + item.Detail)))));
            }
            start = end;
        }
        return entries;
    }

    private Brush TaskBrush(string key)
    {
        var resources = Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries.Last();
        string theme = _themeSettings.HighContrast ? "HighContrast" : RootGrid.ActualTheme == ElementTheme.Light ? "Light" : "Default";
        return (Brush)((ResourceDictionary)resources.ThemeDictionaries[theme])[key];
    }
    private void ResetTaskTheme()
    {
        _renderedTaskSequence = ulong.MaxValue;
        if (TasksPanel.Visibility == Visibility.Visible) _ = RefreshTasksAsync();
    }
    private static string TaskState(JobState state) => state switch
    {
        JobState.Queued => "等待中",
        JobState.Running => "正在识别",
        JobState.WaitingPermission => "等待确认",
        JobState.Completed => "已完成",
        JobState.Failed => "未完成",
        JobState.Cancelled => "已取消",
        _ => "已中断"
    };
    private async void TaskCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTask is not { } job || _jobs is null) return;
        try { await _jobs.CancelAsync(job.Id); await RefreshTasksAsync(); }
        catch (Exception error) { AppLog.Error("Task.Cancel", error); ViewModel.StatusMessage = "无法取消，请重试。"; }
    }
    private async void TaskPermission_Click(object sender, RoutedEventArgs e)
    { if (_selectedTask?.PendingPermission is { } review) await ReviewPermissionAsync(_selectedTask.Id, review); }
    private async void TaskLibrary_Click(object sender, RoutedEventArgs e) => await ShowQuestionLibraryAsync();
    private void TaskUsage_Click(object sender, RoutedEventArgs e)
    {
        TaskUsageFlyout.ShowAt(TaskUsageButton);
    }
    private void TaskLatest_Click(object sender, RoutedEventArgs e) { _taskFollowLatest = true; ScrollToLatestTaskStep(); }
    private void ScrollToLatestTaskStep()
    {
        if (_taskScrollScheduled) return;
        _taskScrollScheduled = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_closed && _taskFollowLatest && TaskLog.Children.LastOrDefault() is FrameworkElement last)
            {
                TaskLogScroll.UpdateLayout();
                // Keep the current step heading visible even when its expanded reply is taller than the viewport.
                double top = last.TransformToVisual(TaskLog).TransformPoint(new(0, 0)).Y;
                TaskLogScroll.ChangeView(null, Math.Min(top, TaskLogScroll.ScrollableHeight), null, true);
            }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            { _taskScrollHeight = TaskLogScroll.ScrollableHeight; _taskScrollScheduled = false; });
        });
    }
    private void TaskLog_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        double height = TaskLogScroll.ScrollableHeight;
        // Reflow can change the viewport before the content height changes. Preserve
        // follow-latest across both phases rather than treating that as manual scrolling.
        bool resized = Math.Abs(height - _taskScrollHeight) > 1
            || Math.Abs(TaskLogScroll.ActualWidth - _taskScrollWidth) > 1
            || Math.Abs(TaskLogScroll.ActualHeight - _taskViewportHeight) > 1;
        _taskScrollHeight = height;
        _taskScrollWidth = TaskLogScroll.ActualWidth;
        _taskViewportHeight = TaskLogScroll.ActualHeight;
        if (_taskScrollScheduled) return;
        if (resized) { if (_taskFollowLatest) ScrollToLatestTaskStep(); }
        else _taskFollowLatest = height - TaskLogScroll.VerticalOffset < 64;
    }
    private void TaskLog_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (_taskFollowLatest) ScrollToLatestTaskStep();
    }

    private static string TaskStateGlyph(JobState state) => state switch
    { JobState.Completed => "\uE73E", JobState.Failed or JobState.Interrupted => "\uE7BA", JobState.Cancelled => "\uE711", JobState.WaitingPermission => "\uE8D7", _ => "\uE916" };

    private sealed class TaskLogEntryView
    {
        public Grid Root { get; } = new() { ColumnSpacing = 8, Padding = new Thickness(0, 0, 0, 12) };
        private readonly FontIcon _icon = new() { FontSize = 13, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 4, 0, 0) };
        private readonly TextBlock _heading = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12, LineHeight = 22 };
        private readonly Run _title = new();
        private readonly Run _result = new();
        private readonly Run _state = new();
        private readonly Run _time = new();
        private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 24, Margin = new Thickness(0, 6, 0, 0) };
        private readonly TextBlock _output = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 14, LineHeight = 24, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
        public bool OutputFitsContent => _output.ActualHeight > 0 && Math.Abs(_output.ActualHeight + _output.Margin.Top + _output.Margin.Bottom - _output.DesiredSize.Height) < 1;
        public bool OutputHasTrailingBlankLines => _output.Text.EndsWith("\n", StringComparison.Ordinal);
        public bool OutputVisible => _output.Visibility == Visibility.Visible;
        public string Title => _title.Text;
        public TaskLogEntryView()
        {
            Root.ColumnDefinitions.Add(new() { Width = new GridLength(16) });
            Root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            for (int i = 0; i < 3; i++) Root.RowDefinitions.Add(new() { Height = GridLength.Auto });
            Root.Children.Add(_icon);
            _heading.Inlines.Add(_title); _heading.Inlines.Add(_result); _heading.Inlines.Add(_state); _heading.Inlines.Add(_time);
            Grid.SetColumn(_heading, 1); Root.Children.Add(_heading);
            Grid.SetRow(_detail, 1); Grid.SetColumnSpan(_detail, 2); Root.Children.Add(_detail);
            Grid.SetRow(_output, 2); Grid.SetColumnSpan(_output, 2); Root.Children.Add(_output);
        }
        public void Update(JobActivity step, string state, ulong time, Func<string, Brush> brush, string? tooltip = null)
        {
            bool model = step.Kind == "model";
            bool error = state is "failed" or "cancelled";
            var secondary = brush("TaskSecondaryBrush");
            _icon.Foreground = error ? brush("TaskErrorBrush") : secondary;
            _icon.Glyph = error ? "\uE7BA" : step.Kind switch
            { "model" => "\uE945", "read_page" => "\uE8A5", "save_question" => "\uE74E", "save_references" => "\uE8F1", "finish" => "\uE73E", "python" or "shell" => "\uE8EF", _ => "\uE9F9" };
            string output = model ? step.Text.TrimEnd() : "";
            string stateLabel = state switch { "completed" => "已完成", "failed" => "未完成", "cancelled" => "已取消", "waiting" => "等待确认", _ => "进行中" };
            string timestamp = $"{DateTimeOffset.FromUnixTimeMilliseconds((long)time).ToLocalTime():HH:mm:ss}";
            _title.Text = (model ? "识别" : step.Title) + (model && step.Text.Length >= 16000 ? "（输出节选）" : "");
            _result.Text = !model && !string.IsNullOrWhiteSpace(step.Detail) ? "  " + step.Detail : "";
            _state.Text = !model && (state is "completed" or "waiting") && !string.IsNullOrWhiteSpace(step.Detail) ? "" : "  " + stateLabel;
            _time.Text = "  " + timestamp + (step.ElapsedMs > 0 ? $"  {step.ElapsedMs / 1000d:0.0} 秒" : "");
            _heading.Foreground = secondary;
            _state.Foreground = error ? brush("TaskErrorBrush") : secondary;
            _detail.Text = step.Detail;
            _detail.Foreground = secondary;
            _detail.Visibility = model && output.Length == 0 && !string.IsNullOrWhiteSpace(step.Detail) ? Visibility.Visible : Visibility.Collapsed;
            if (_output.Text != output) _output.Text = output;
            _output.Visibility = output.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            // Short actions read as log lines; model replies read as continuous prose.
            Root.Padding = new Thickness(0, 0, 0, model && (output.Length > 0 || _detail.Visibility == Visibility.Visible) ? 20 : 8);
            ToolTipService.SetToolTip(_heading, tooltip ?? (model && step.Text.Length >= 16000 ? "输出节选" : null));
            AutomationProperties.SetHelpText(Root, tooltip ?? "");
            AutomationProperties.SetName(Root, _title.Text + "，" + stateLabel + "，" + timestamp);
        }
    }
}
