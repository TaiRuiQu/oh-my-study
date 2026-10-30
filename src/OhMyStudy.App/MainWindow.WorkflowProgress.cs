using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private readonly List<WorkflowStepView> workflowSteps = [];
    private readonly Dictionary<string, (Grid Root, TextBlock Number, TextBlock Solve, TextBlock Grade)> workflowQuestions = [];

    private void UpdateWorkflowProgress(Job job, IReadOnlyList<JobActivity> activities)
    {
        bool references = job.Kind == DiagnosticKind.References;
        int totalPages = job.Input?.Document.Pages.Count(p => !references || p.Role == "reference") ?? 0;
        int read = activities.Select(a => a.PagesRead).DefaultIfEmpty().Max();
        int total = activities.Select(a => a.QuestionsSaved).DefaultIfEmpty().Max();
        var solves = activities.Where(a => a.Kind == "solve").ToArray();
        var grades = activities.Where(a => a.Kind == "assess").ToArray();
        var classification = activities.FirstOrDefault(a => a.Key == "classify");
        var layout = classification?.ExamLayout;
        var processingMode = layout?.ResolveMode(job.Input!.Document.Mode) ?? job.Input?.Document.Mode;
        int solved = solves.Count(a => a.State == "completed");
        int needsReview = grades.Count(a => a.State == "completed" && a.Detail == "Answer matching or grading needs review.");
        int graded = grades.Count(a => a.State == "completed" && a.Detail is not ("Grading not required." or "Answer matching or grading needs review."));
        int skipped = grades.Count(a => a.Detail == "Grading not required.");
        bool ocrDone = activities.Any(a => a.Key == "ocr" && a.State == "completed");
        bool splitDone = activities.Any(a => a.Key == "split" && a.State == "completed");
        string State(bool started, bool complete, bool skip = false)
            => skip ? "skipped" : complete ? "completed" : !started ? "pending"
                : job.State.IsTerminal() ? job.State == JobState.Cancelled ? "cancelled" : "failed" : "running";
        var splitter = job.Input?.Splitter;
        var grader = job.Input?.Grader;
        var steps = new[]
        {
            ("Identify exam format", State(classification is not null, classification?.State == "completed", references || classification is null && job.State.IsTerminal()),
                references ? UiText.Get("Reference import skips format classification") : layout is null
                ? (job.Input?.Classifier is { } classifier ? classifier.Provider.Name + " / " + classifier.Model.Label : UiText.Get("Use the splitting model"))
                : UiText.Format("{0} · confidence {1:P0}", UiText.ExamFormatName(layout.Format), layout.Confidence)),
            ("Upload and recognize", State(activities.Any(a => a.Kind is "upload" or "ocr"), ocrDone),
                ocrDone ? UiText.Format("Recognized {0}/{1} pages · Markdown, images and tables saved", read, totalPages)
                : activities.LastOrDefault(a => a.Key == "upload") is { State: "running" } upload ? UiText.ActivityText(upload.Detail)
                : UiText.Format("Recognized {0}/{1} pages · MinerU", read, totalPages)),
            ("Split and bind materials", State(activities.Any(a => a.Kind == "split"), splitDone),
                splitDone ? references ? UiText.Get("Reference answers matched to questions") : UiText.Format("{0} questions · source materials bound", total)
                : (splitter is null ? UiText.Get("Waiting for OCR materials") : splitter.Provider.Name + " / " + splitter.Model.Label)),
            ("Solve in parallel", State(solves.Length > 0, splitDone && solved == total, references),
                references ? UiText.Get("Reference import does not solve questions") : UiText.Format("Solved {0}/{1} · up to {2} questions at once", solved, total, job.Input?.Workflow?.ParallelQuestions ?? 3)),
            ("Grade student work", State(grades.Length > 0, splitDone && grades.Count(a => a.State == "completed") == total,
                    references || processingMode == ImportMode.Blank || splitDone && total > 0 && skipped == total),
                references ? UiText.Get("Reference import does not grade student work")
                : processingMode == ImportMode.Blank ? UiText.Get("Blank exam · no student work to grade")
                : UiText.Format("Graded {0} · review {1} · not required {2} · {3}", graded, needsReview, skipped, grader is null ? UiText.Get("Use the solving model") : grader.Provider.Name + " / " + grader.Model.Label))
        };
        while (workflowSteps.Count < steps.Length)
        {
            var view = new WorkflowStepView(workflowSteps.Count + 1);
            workflowSteps.Add(view); TaskWorkflowStages.Children.Add(view.Root);
        }
        for (int i = 0; i < steps.Length; i++) workflowSteps[i].Update(steps[i].Item1, steps[i].Item2, steps[i].Item3, TaskBrush);
        var questionActivities = activities.Where(a => a.Kind is "question" or "solve" or "assess" or "result")
            .GroupBy(a => a.Key[(a.Key.IndexOf('-') + 1)..]).ToArray();
        TaskQuestionSection.Visibility = references || questionActivities.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        TaskQuestionSummary.Text = UiText.Format("{0} split · {1} solved · {2} graded · {3} for review · {4} without grading", total, solved, graded, needsReview, skipped);
        var ids = questionActivities.Select(g => g.Key).ToHashSet(StringComparer.Ordinal);
        foreach (string stale in workflowQuestions.Keys.Where(k => !ids.Contains(k)).ToArray())
        { TaskQuestionList.Children.Remove(workflowQuestions[stale].Root); workflowQuestions.Remove(stale); }
        foreach (var group in questionActivities)
        {
            if (!workflowQuestions.TryGetValue(group.Key, out var row))
            {
                var root = new Grid { ColumnSpacing = 12, Padding = new Thickness(0, 10, 0, 10) };
                root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
                TextBlock Text(int column)
                {
                    var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 14 };
                    Grid.SetColumn(text, column); root.Children.Add(text); return text;
                }
                row = (root, Text(0), Text(1), Text(2));
                workflowQuestions.Add(group.Key, row); TaskQuestionList.Children.Add(root);
            }
            var question = group.FirstOrDefault(a => a.Kind == "question");
            row.Number.Text = question is null ? UiText.ActivityText(group.First().Title) : UiText.ActivityText(question.Title);
            string QuestionState(string kind)
            {
                var activity = group.FirstOrDefault(a => a.Kind == kind);
                if (kind == "assess" && (activity?.Detail == "Grading not required." || processingMode == ImportMode.Blank)) return UiText.Get("Not required");
                if (kind == "assess" && activity?.Detail == "Answer matching or grading needs review.") return UiText.Get("Needs review");
                return WorkflowStateLabel(activity is null ? "pending" : State(true, activity.State == "completed"));
            }
            row.Solve.Text = UiText.Format("Solution: {0}", QuestionState("solve"));
            row.Grade.Text = UiText.Format("Grading: {0}", QuestionState("assess"));
            bool failed = group.Any(a => a.Kind == "result" && a.State == "failed");
            row.Number.Foreground = TaskBrush(failed ? "TaskErrorBrush" : "TaskSecondaryBrush");
            row.Solve.Foreground = row.Grade.Foreground = TaskBrush("TaskSecondaryBrush");
            AutomationProperties.SetName(row.Root, row.Number.Text + ", " + row.Solve.Text + ", " + row.Grade.Text);
        }
    }

    private static string WorkflowStateLabel(string state) => UiText.Get(state switch
    { "completed" => "Completed", "running" => "In progress", "failed" => "Stopped", "cancelled" => "Canceled", "skipped" => "Not required", _ => "Waiting" });

    private sealed class WorkflowStepView
    {
        private readonly TextBlock status = new() { FontSize = 14, TextWrapping = TextWrapping.Wrap };
        private readonly string glyph;
        public Controls.WorkflowStep Root { get; } = new();

        public WorkflowStepView(int index)
        {
            glyph = index switch { 1 => "\uE721", 2 => "\uE8A5", 3 => "\uE7A8", 4 => "\uE8EF", _ => "\uE73E" };
            Root.Glyph = glyph; Root.IsLast = index == 5; Root.Fields = status;
        }

        public void Update(string heading, string state, string description, Func<string, Brush> brush)
        {
            Root.Title = UiText.Get(heading); Root.Description = description; status.Text = WorkflowStateLabel(state);
            var color = brush(state is "failed" or "cancelled" ? "TaskErrorBrush" : state is "completed" or "running" ? "TaskAccentBrush" : "TaskSecondaryBrush");
            Root.Glyph = state == "completed" ? "\uE73E" : state is "failed" or "cancelled" ? "\uE7BA" : glyph;
            status.Foreground = color; Root.SetStatus(status.Text, color);
        }
    }
}
