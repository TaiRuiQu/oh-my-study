using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
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
    private readonly Dictionary<string, TaskStepView> _taskSteps = [];
    private string? _timelineJobId;
    private ulong _renderedTaskSequence;
    private bool _taskFollowLatest;
    private double _taskScrollHeight;
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
        bool shortWindow = RootGrid.ActualHeight < 750;
        TaskSidebar.Visibility = !stacked && TaskDetail.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        TaskCompactPicker.Visibility = stacked && TaskDetail.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        TaskSummary.Padding = new Thickness(stacked ? 16 : 24, shortWindow ? 12 : 18, stacked ? 16 : 24, shortWindow ? 12 : 16);
        TaskButtons.Margin = new Thickness(stacked ? 16 : 24, 0, stacked ? 16 : 24, shortWindow ? 12 : 16);
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
        if (_library is null || _timelineJobId == selected.Id && _renderedTaskSequence == selected.Sequence) return;
        var events = await _library.ListJobEventsAsync(selected.Id, _lifetime.Token);
        var calls = await _library.ListJobModelCallsAsync(selected.Id, _lifetime.Token);
        if (_closed || generation != _taskRefreshGeneration || selected.Id != _selectedTaskId) return;
        _selectedTask = selected;
        bool changed = _timelineJobId != selected.Id;
        bool follow = changed ? !selected.State.IsTerminal() : _taskFollowLatest;
        if (changed) _taskFollowLatest = follow;
        if (changed) { _taskSteps.Clear(); TaskTimeline.Children.Clear(); TaskTimelineScroll.ChangeView(null, 0, null, true); }
        _timelineJobId = selected.Id; _renderedTaskSequence = selected.Sequence;
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
        TaskRounds.Text = Math.Max(calls.Count, activities.Values.Count(v => v.Activity.Kind == "model")).ToString();
        TaskStateLabel.Text = TaskState(selected.State);
        TaskStateLabel.Foreground = TaskBrush(selected.State is JobState.Failed or JobState.Interrupted ? "TaskErrorBrush" : "TaskAccentBrush");
        TaskHeroIcon.Glyph = selected.State switch { JobState.Completed => "\uE73E", JobState.Failed or JobState.Interrupted => "\uE7BA", JobState.Cancelled => "\uE711", _ => "\uE9F9" };
        TaskHeroIcon.Foreground = TaskStateLabel.Foreground;
        TaskProgress.Visibility = selected.State == JobState.Running ? Visibility.Visible : Visibility.Collapsed;
        TaskCancelButton.Visibility = selected.State.IsTerminal() ? Visibility.Collapsed : Visibility.Visible;
        TaskPermissionButton.Visibility = selected.PendingPermission is null ? Visibility.Collapsed : Visibility.Visible;
        ulong inputTokens = 0, outputTokens = 0, cacheReadTokens = 0; bool inputKnown = true, outputKnown = true, cacheKnown = true;
        double cost = 0; bool priced = calls.Count > 0;
        foreach (var call in calls)
        {
            if (call.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number && a.TryGetUInt64(out var ai)) inputTokens += ai; else inputKnown = false;
                if (usage.TryGetProperty("output", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number && b.TryGetUInt64(out var bi)) outputTokens += bi; else outputKnown = false;
                if (usage.TryGetProperty("cache_read", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number && r.TryGetUInt64(out var ri)) cacheReadTokens += ri; else cacheKnown = false;
            }
            if (!call.TryGetProperty("usage", out _)) { inputKnown = false; outputKnown = false; cacheKnown = false; }
            if (call.TryGetProperty("estimated_cost", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number && c.TryGetDouble(out var amount)) cost += amount; else priced = false;
        }
        string usageLabel = "";
        TaskInputUsage.Text = inputKnown ? $"输入 {inputTokens:N0}" : "输入未提供"; TaskOutputUsage.Text = outputKnown ? $"输出 {outputTokens:N0}" : "输出未提供";
        TaskInputCached.Visibility = calls.Count > 0 && inputKnown && cacheKnown && inputTokens > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskInputCached.Text = cacheReadTokens == 0 ? "其中没有缓存输入" : $"其中缓存输入 {cacheReadTokens:N0}";
        TaskUsageDetails.Visibility = calls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskTokenUsage.Visibility = calls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskCostRow.Visibility = priced ? Visibility.Visible : Visibility.Collapsed;
        TaskUsage.Text = "";
        if (priced) _ = UpdateTaskCnyCostAsync(selected.Id, generation, usageLabel, cost, selected.Input?.Model.Currency ?? "USD");
        TaskUsage.Visibility = calls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        foreach (var (activity, time) in activities.Values)
        {
            if (!_taskSteps.TryGetValue(activity.Key, out var view))
            { view = new TaskStepView(); view.SetOutputHeight(TaskTimelineScroll.ActualHeight); _taskSteps.Add(activity.Key, view); TaskTimeline.Children.Add(view.Root); }
            string state = activity.State;
            if (state == "running" && selected.State.IsTerminal()) state = selected.State == JobState.Completed ? "completed" : selected.State == JobState.Cancelled ? "cancelled" : "failed";
            view.Update(activity, state, time, TaskBrush);
        }
        if (follow) { _taskFollowLatest = true; ScrollToLatestTaskStep(); }
        ApplyTaskLayout();
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
    private void TaskLatest_Click(object sender, RoutedEventArgs e) { _taskFollowLatest = true; ScrollToLatestTaskStep(); }
    private void ScrollToLatestTaskStep()
    {
        if (_taskScrollScheduled) return;
        _taskScrollScheduled = true;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (!_closed && _taskFollowLatest && TaskTimeline.Children.LastOrDefault() is FrameworkElement last)
            {
                TaskTimelineScroll.UpdateLayout();
                // Keep the current step heading visible even when its expanded reply is taller than the viewport.
                double top = last.TransformToVisual(TaskTimeline).TransformPoint(new(0, 0)).Y;
                TaskTimelineScroll.ChangeView(null, Math.Min(top, TaskTimelineScroll.ScrollableHeight), null, true);
            }
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
            { _taskScrollHeight = TaskTimelineScroll.ScrollableHeight; _taskScrollScheduled = false; });
        });
    }
    private void TaskTimeline_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
    {
        double height = TaskTimelineScroll.ScrollableHeight;
        bool resized = Math.Abs(height - _taskScrollHeight) > 1;
        _taskScrollHeight = height;
        if (_taskScrollScheduled) return;
        if (resized) { if (_taskFollowLatest) ScrollToLatestTaskStep(); }
        else _taskFollowLatest = height - TaskTimelineScroll.VerticalOffset < 64;
    }
    private void TaskTimeline_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        foreach (var step in _taskSteps.Values) step.SetOutputHeight(e.NewSize.Height);
    }

    private static string TaskStateGlyph(JobState state) => state switch
    { JobState.Completed => "\uE73E", JobState.Failed or JobState.Interrupted => "\uE7BA", JobState.Cancelled => "\uE711", JobState.WaitingPermission => "\uE8D7", _ => "\uE916" };

    private sealed class TaskStepView
    {
        public Grid Root { get; } = new() { ColumnSpacing = 14, Padding = new Thickness(0, 0, 0, 20) };
        private readonly Border _badge = new() { Width = 28, Height = 28, CornerRadius = new CornerRadius(14), VerticalAlignment = VerticalAlignment.Top };
        private readonly FontIcon _icon = new() { FontSize = 14 };
        private readonly TextBlock _title = new() { FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap };
        private readonly TextBlock _detail = new() { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
        private readonly TextBlock _time = new() { FontSize = 12 };
        private readonly TextBlock _state = new() { FontSize = 12 };
        private readonly TextBlock _duration = new() { FontSize = 12 };
        private readonly FontIcon _stateIcon = new() { FontSize = 11 };
        private readonly StackPanel _durationGroup;
        private readonly TextBlock _output = new() { TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true, FontSize = 13, LineHeight = 22, VerticalAlignment = VerticalAlignment.Top };
        private readonly ScrollViewer _outputScroll = new() { MaxHeight = 220, VerticalAlignment = VerticalAlignment.Top, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 4, 0, 0) };
        private readonly HyperlinkButton _toggle = new() { Content = "展开回复", HorizontalAlignment = HorizontalAlignment.Left, Padding = new Thickness(0, 2, 0, 2), MinHeight = 24, Visibility = Visibility.Collapsed };
        private bool _hadOutput;
        private string _replyLabel = "回复";
        public bool OutputFitsContent => _outputScroll.ActualHeight <= _output.ActualHeight + 1;
        public bool OutputHasTrailingBlankLines => _output.Text.EndsWith("\n", StringComparison.Ordinal);
        public void Expand() { _outputScroll.Visibility = Visibility.Visible; UpdateToggle(); }
        private void UpdateToggle() => _toggle.Content = (_outputScroll.Visibility == Visibility.Visible ? "收起" : "展开") + _replyLabel;
        public void SetOutputHeight(double viewportHeight) => _outputScroll.MaxHeight = Math.Clamp(viewportHeight - 150, 44, 260);
        public TaskStepView()
        {
            Root.ColumnDefinitions.Add(new() { Width = new GridLength(28) }); Root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
            _badge.Child = _icon; Root.Children.Add(_badge);
            var line = new Border { Width = 1, Background = new SolidColorBrush(Windows.UI.Color.FromArgb(50, 128, 128, 128)), Margin = new Thickness(0, 34, 0, -14), HorizontalAlignment = HorizontalAlignment.Center };
            Root.Children.Insert(0, line);
            var body = new StackPanel { Spacing = 6, Padding = new Thickness(0, 3, 0, 0) };
            var metadata = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14 };
            metadata.Children.Add(Group(new FontIcon { Glyph = "\uE917", FontSize = 11 }, _time)); metadata.Children.Add(Group(_stateIcon, _state));
            _durationGroup = Group(new FontIcon { Glyph = "\uE916", FontSize = 11 }, _duration); metadata.Children.Add(_durationGroup);
            body.Children.Add(_title); body.Children.Add(_detail); body.Children.Add(metadata);
            _outputScroll.Content = _output;
            _toggle.Click += (_, _) => { _outputScroll.Visibility = _outputScroll.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible; UpdateToggle(); };
            body.Children.Add(_toggle); body.Children.Add(_outputScroll); Grid.SetColumn(body, 1); Root.Children.Add(body);
        }
        private static StackPanel Group(FontIcon icon, TextBlock text)
        { var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 5 }; row.Children.Add(icon); row.Children.Add(text); return row; }
        public void Update(JobActivity step, string state, ulong time, Func<string, Brush> brush)
        {
            _badge.Background = brush("TaskAccentBackgroundBrush");
            _icon.Foreground = _stateIcon.Foreground = brush(state == "failed" ? "TaskErrorBrush" : "TaskAccentBrush");
            _icon.Glyph = state is "failed" or "cancelled" ? "\uE7BA" : step.Kind switch
            { "model" => "\uE945", "read_page" => "\uE8A5", "save_question" => "\uE74E", "save_references" => "\uE8F1", "finish" => "\uE73E", "python" or "shell" => "\uE8EF", _ => "\uE9F9" };
            _title.Text = step.Title; _detail.Text = step.Detail; _detail.Visibility = string.IsNullOrWhiteSpace(step.Detail) ? Visibility.Collapsed : Visibility.Visible;
            _time.Text = $"{DateTimeOffset.FromUnixTimeMilliseconds((long)time).ToLocalTime():HH:mm:ss}";
            _state.Text = state switch { "completed" => "已完成", "failed" => "未完成", "cancelled" => "已取消", _ => "进行中" };
            _stateIcon.Glyph = state switch { "completed" => "\uE73E", "failed" => "\uE7BA", "cancelled" => "\uE711", _ => "\uE916" };
            _duration.Text = $"{step.ElapsedMs / 1000d:0.0} 秒"; _durationGroup.Visibility = step.ElapsedMs > 0 ? Visibility.Visible : Visibility.Collapsed;
            _detail.Foreground = _time.Foreground = _state.Foreground = _duration.Foreground = brush("TaskSecondaryBrush");
            string output = step.Text.TrimEnd(); if (_output.Text != output) _output.Text = output;
            if (!_hadOutput && output.Length > 0) { if (step.Kind == "model" && state == "running") Expand(); _hadOutput = true; }
            _replyLabel = (step.Kind == "model" ? "回复" : "输出") + (step.Text.Length >= 16000 ? "（节选）" : ""); UpdateToggle();
            _toggle.Visibility = output.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
            if (output.Length == 0) _outputScroll.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(Root, step.Title + "，" + _state.Text + "，" + _time.Text);
        }
    }
}
