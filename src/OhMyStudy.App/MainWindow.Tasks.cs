using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
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
    private bool _retryingTask;

    private void TasksPanel_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyTaskLayout();
    private void ApplyTaskLayout()
    {
        if (TaskListColumn is null) return;
        bool stacked = TasksPanel.ActualWidth < 850;
        TaskButtons.Orientation = TasksPanel.ActualWidth < (UiText.Language == "zh-CN" ? 650 : 1100) ? Orientation.Vertical : Orientation.Horizontal;
        TaskListColumn.Width = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(Math.Clamp(TaskColumnSplitter.ColumnSize ?? 300, 220, Math.Max(220, TasksPanel.ActualWidth - 372)));
        TasksPanel.ColumnDefinitions[1].Width = new GridLength(stacked ? 0 : 12);
        TasksPanel.ColumnDefinitions[2].Width = stacked ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        TaskColumnSplitter.Visibility = !stacked && TaskDetail.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        TaskListRow.Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        TasksPanel.RowDefinitions[1].Height = stacked ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Grid.SetColumn(TaskDetail, stacked ? 0 : 2); Grid.SetRow(TaskDetail, stacked ? 1 : 0);
        bool compactHeading = TaskDetail.ActualWidth < 600;
        Grid.SetColumnSpan(TaskHeading, compactHeading ? 2 : 1);
        Grid.SetColumn(TaskButtons, compactHeading ? 0 : 1);
        Grid.SetRow(TaskButtons, compactHeading ? 1 : 0);
        Grid.SetColumnSpan(TaskButtons, compactHeading ? 2 : 1);
        TaskButtons.HorizontalAlignment = compactHeading ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        TaskButtons.Margin = new Thickness(0, compactHeading ? 12 : 0, 0, 0);
        TaskSidebar.Visibility = !stacked && TaskDetail.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
        TaskCompactPicker.Visibility = stacked && TaskDetail.Visibility == Visibility.Visible ? Visibility.Visible : Visibility.Collapsed;
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
        jobs = jobs.Where(j => j.Input?.Splitter is not null).ToList();
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
                    var title = new TextBlock { FontSize = 16, TextWrapping = TextWrapping.Wrap, MaxLines = 2, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
                    var status = new TextBlock { Style = (Style)TasksPanel.Resources["QuietTextStyle"], TextWrapping = TextWrapping.Wrap };
                    var content = new StackPanel { Spacing = 6, Padding = new Thickness(4, 10, 4, 10) };
                    var statusIcon = new FontIcon { FontSize = 12 }; var time = new TextBlock { Style = (Style)TasksPanel.Resources["QuietTextStyle"] };
                    var metadata = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 7 };
                    metadata.Children.Add(statusIcon); metadata.Children.Add(status);
                    metadata.Children.Add(new FontIcon { Glyph = "\uE787", FontSize = 12, Margin = new Thickness(6, 0, 0, 0) }); metadata.Children.Add(time);
                    content.Children.Add(title); content.Children.Add(metadata);
                    var item = new ListViewItem { Content = content, Tag = job.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    var compactTitle = new TextBlock { FontSize = 16, TextTrimming = TextTrimming.CharacterEllipsis };
                    var compact = new ComboBoxItem { Content = compactTitle, Tag = job.Id, HorizontalContentAlignment = HorizontalAlignment.Stretch };
                    row = (item, title, status, statusIcon, time, compact, compactTitle); _taskItems.Add(job.Id, row);
                }
                row.Title.Text = job.Input?.Document.Name ?? UiText.Get("Organize materials");
                row.Status.Text = TaskState(job.State);
                row.StatusIcon.Glyph = TaskStateGlyph(job.State);
                row.StatusIcon.Foreground = TaskBrush(job.State is JobState.Failed or JobState.Interrupted ? "TaskErrorBrush" : "TaskAccentBrush");
                row.Time.Text = DateTimeOffset.FromUnixTimeMilliseconds((long)job.CreatedAt).ToLocalTime().ToString("g", UiText.Culture);
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
        TaskExportRecognition.Visibility = selected.Input is not null && selected.State.IsTerminal() ? Visibility.Visible : Visibility.Collapsed;
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
        if (activities.Count == 0)
            activities["queued"] = (new JobActivity { Key = "queued", Kind = "state", Title = TaskState(selected.State), Detail = selected.Message }, selected.CreatedAt);
        UpdateWorkflowProgress(selected, activities.Values.Select(v => v.Activity).ToArray());
        int pages = activities.Values.Max(v => v.Activity.PagesRead);
        int questions = activities.Values.Max(v => v.Activity.QuestionsSaved);
        bool hasTrace = events.Any(e => e.Kind == "activity");
        TaskTitle.Text = selected.Input?.Document.Name ?? UiText.Get("Organize materials");
        TaskSubtitle.Text = selected.State switch
        {
            JobState.Completed => UiText.Get("Recognition complete. Questions added to the question bank."),
            JobState.Cancelled => UiText.Get("Task canceled. Saved questions are preserved."),
            JobState.Failed => activities.Values.LastOrDefault(item => item.Activity.State == "failed").Activity?.Detail ?? UiText.Get("Recognition incomplete. Saved questions are preserved. Try recognition again in Exams."),
            JobState.Interrupted => UiText.Get("Recognition interrupted. Saved questions are preserved. Try again in Exams."),
            JobState.WaitingPermission => UiText.Get("Confirm an action to continue."),
            _ => activities.Values.Last().Activity.Detail
        };
        TaskSubtitle.Text = UiText.ActivityText(TaskSubtitle.Text);
        TaskPages.Text = hasTrace ? $"{pages} / {selected.Input?.Document.Pages.Count ?? pages}" : "—";
        TaskQuestions.Text = hasTrace ? questions.ToString() : "—";
        TaskModelCalls.Text = UiText.Format("Model calls: {0}", Math.Max(calls.Count, activities.Values.Count(v => v.Activity.Kind == "model")));
        TaskStateLabel.Text = TaskState(selected.State);
        TaskStateLabel.Foreground = TaskBrush(selected.State is JobState.Failed or JobState.Interrupted ? "TaskErrorBrush" : "TaskAccentBrush");
        TaskHeroIcon.Glyph = selected.State switch { JobState.Completed => "\uE73E", JobState.Failed or JobState.Interrupted => "\uE7BA", JobState.Cancelled => "\uE711", _ => "\uE9F9" };
        TaskHeroIcon.Foreground = TaskStateLabel.Foreground;
        TaskProgress.Visibility = selected.State == JobState.Running ? Visibility.Visible : Visibility.Collapsed;
        TaskCancelButton.Visibility = selected.State.IsTerminal() ? Visibility.Collapsed : Visibility.Visible;
        TaskRetryButton.Visibility = selected.State is JobState.Failed or JobState.Interrupted ? Visibility.Visible : Visibility.Collapsed;
        TaskRetryButton.IsEnabled = !_retryingTask && !jobs.Any(j => !j.State.IsTerminal() && j.Input?.Document.Id == selected.Input?.Document.Id);
        TaskPermissionButton.Visibility = selected.PendingPermission is null ? Visibility.Collapsed : Visibility.Visible;
        ulong inputTokens = 0, outputTokens = 0, cacheReadTokens = 0, cacheWriteTokens = 0;
        bool inputKnown = true, outputKnown = true, cacheKnown = true, cacheWriteKnown = true;
        var costs = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var call in calls)
        {
            if (call.TryGetProperty("usage", out var usage))
            {
                if (usage.TryGetProperty("input", out var a) && a.ValueKind == System.Text.Json.JsonValueKind.Number && a.TryGetUInt64(out var ai)) inputTokens += ai; else inputKnown = false;
                if (usage.TryGetProperty("output", out var b) && b.ValueKind == System.Text.Json.JsonValueKind.Number && b.TryGetUInt64(out var bi)) outputTokens += bi; else outputKnown = false;
                if (usage.TryGetProperty("cache_read", out var r) && r.ValueKind == System.Text.Json.JsonValueKind.Number && r.TryGetUInt64(out var ri)) cacheReadTokens += ri; else cacheKnown = false;
                if (usage.TryGetProperty("cache_write", out var w) && w.ValueKind == System.Text.Json.JsonValueKind.Number && w.TryGetUInt64(out var wi)) cacheWriteTokens += wi; else cacheWriteKnown = false;
            }
            if (!call.TryGetProperty("usage", out _)) { inputKnown = false; outputKnown = false; cacheKnown = false; cacheWriteKnown = false; }
            if (call.TryGetProperty("estimated_cost", out var c) && c.ValueKind == System.Text.Json.JsonValueKind.Number && c.TryGetDouble(out var amount))
            {
                string currency = call.TryGetProperty("currency", out var unit) ? unit.GetString() ?? "" : "";
                costs[currency] = costs.GetValueOrDefault(currency) + amount;
            }
        }
        bool priced = costs.Count > 0;
        TaskInputUsage.Text = inputKnown ? UiText.Format("Input {0:N0}", inputTokens) : UiText.Get("Input usage not provided"); TaskOutputUsage.Text = outputKnown ? UiText.Format("Output {0:N0}", outputTokens) : UiText.Get("Output usage not provided");
        TaskInputUncached.Text = inputKnown && cacheKnown && cacheReadTokens <= inputTokens ? UiText.Format("Uncached {0:N0}", inputTokens - cacheReadTokens) : UiText.Get("Uncached usage not provided");
        TaskInputCached.Text = cacheKnown ? UiText.Format("Cache read {0:N0}", cacheReadTokens) : UiText.Get("Cache read usage not provided");
        TaskInputCacheWrite.Text = cacheWriteKnown ? UiText.Format("Cache write {0:N0}", cacheWriteTokens) : UiText.Get("Cache write usage not provided");
        TaskNoUsage.Visibility = calls.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskTokenUsage.Visibility = calls.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        TaskUsage.Text = "—";
        TaskRateNote.Text = priced ? UiText.Get("Calculating cost…") : calls.Count > 0 ? UiText.Get("Cannot estimate cost: pricing or usage details are incomplete.") : UiText.Get("No cost records yet");
        if (priced) _ = UpdateTaskCnyCostAsync(selected.Id, generation, costs);
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
            var (activity, time, tooltip, members) = entries[i];
            if (!_taskSteps.TryGetValue(activity.Key, out var view))
            { view = new TaskLogEntryView(this); _taskSteps.Add(activity.Key, view); }
            if (i >= TaskLog.Children.Count || !ReferenceEquals(TaskLog.Children[i], view.Root))
            { TaskLog.Children.Remove(view.Root); TaskLog.Children.Insert(i, view.Root); }
            view.Update(activity, activity.State, time, TaskBrush, tooltip, members);
        }
        if (follow) { _taskFollowLatest = true; ScrollToLatestTaskStep(); }
        ApplyTaskLayout();
    }

    private static List<(JobActivity Activity, ulong Time, string? Tooltip, IReadOnlyList<JobActivity> Members)> GroupTaskLogActivities(IReadOnlyList<(JobActivity Activity, ulong Time)> activities)
    {
        // Empty successful model turns are bookkeeping, not readable content. They
        // must not split a run of tools, even when the calls came from many turns.
        activities = activities.Where(item => item.Activity.Kind != "model" || item.Activity.State != "completed" || !string.IsNullOrWhiteSpace(item.Activity.Text)).ToArray();
        var entries = new List<(JobActivity Activity, ulong Time, string? Tooltip, IReadOnlyList<JobActivity> Members)>();
        for (int start = 0; start < activities.Count;)
        {
            var (first, time) = activities[start];
            int end = start + 1;
            // Meaningful replies, pending requests and failures retain their place.
            if (first.State == "completed" && first.Kind is not ("model" or "state"))
                while (end < activities.Count && activities[end].Activity.Kind == first.Kind && activities[end].Activity.State == "completed") end++;
            int count = end - start;
            if (count == 1) entries.Add((first, time, null, new[] { first }));
            else
            {
                var members = activities.Skip(start).Take(count).Select(item => item.Activity).ToArray();
                string title = first.Kind switch
                {
                    "read_page" or "read_pages" => UiText.Get("Read pages"),
                    "save_question" or "save_questions" => UiText.Get("Save questions"),
                    "save_references" => UiText.Get("Save reference answers"),
                    "python" => UiText.Get("Run Python"),
                    "shell" => UiText.Get("Run PowerShell"),
                    _ => UiText.ActivityText(first.Title)
                };
                entries.Add((first with { Title = UiText.Format("{0} (×{1})", title, count), Detail = "", Text = "", ElapsedMs = members.Sum(item => item.ElapsedMs) }, time,
                    string.Join("\n", members.Select(item => UiText.ActivityText(item.Title) + (string.IsNullOrWhiteSpace(item.Detail) ? "" : ": " + UiText.ActivityText(item.Detail)))), members));
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
        JobState.Queued => UiText.Get("Queued"),
        JobState.Running => UiText.Get("Recognizing"),
        JobState.WaitingPermission => UiText.Get("Awaiting confirmation"),
        JobState.Completed => UiText.Get("Completed"),
        JobState.Failed => UiText.Get("Incomplete"),
        JobState.Cancelled => UiText.Get("Canceled"),
        _ => UiText.Get("Interrupted")
    };
    private async void TaskCancel_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedTask is not { } job || _jobs is null) return;
        try { await _jobs.CancelAsync(job.Id); await RefreshTasksAsync(); }
        catch (Exception error) { AppLog.Error("Task.Cancel", error); ViewModel.StatusMessage = UiText.Get("Could not cancel. Try again."); }
    }
    private async void TaskPermission_Click(object sender, RoutedEventArgs e)
    { if (_selectedTask?.PendingPermission is { } review) await ReviewPermissionAsync(_selectedTask.Id, review); }
    private async void TaskRetry_Click(object sender, RoutedEventArgs e)
    {
        if (_retryingTask || _selectedTask is not { } job || _jobs is null || _library is null) return;
        _retryingTask = true; TaskRetryButton.IsEnabled = false;
        try
        {
            if (_questionEditorOpen || ViewModel.HasUnsavedChanges || HasUnsavedSettingsChanges)
            { ViewModel.StatusMessage = UiText.Get("Save or close the editor first."); return; }
            GradingInput? input = null;
            ExecutionPolicy? policy = null;
            if (job.Input is { } original)
            {
                var processing = await ChooseProcessingAsync(job.Kind == DiagnosticKind.References, false, retry: true);
                if (processing is null) return;
                var document = await _library.GetDocumentAsync(original.Document.Id, _lifetime.Token);
                input = CreateWorkflowInput(document, processing);
                policy = processing.Policy;
            }
            var replacement = await _jobs.RetryAsync(job.Id, policy, input, _lifetime.Token);
            _selectedTaskId = replacement.Id;
            AppLog.Info("Task.Retry", $"PreviousJob={job.Id}; NewJob={replacement.Id}; Document={input?.Document.Id}");
            await RefreshTasksAsync();
            await RefreshDocumentsAsync();
            ViewModel.StatusMessage = UiText.Get("Added to queue.");
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
        catch (Exception error)
        {
            AppLog.Failure("Task.Retry", $"Job={job.Id}; Document={job.Input?.Document.Id}; {error}");
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                RequestedTheme = RootGrid.ActualTheme,
                Title = UiText.Get("Could not retry the task"),
                Content = UiText.Get(error.Message) == error.Message ? UiText.Get("Check model settings and make sure no other task is processing this exam, then try again.") : UiText.Get(error.Message),
                CloseButtonText = UiText.Get("Close")
            };
            await dialog.ShowAsync();
        }
        finally { _retryingTask = false; TaskRetryButton.IsEnabled = true; }
    }
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

}
