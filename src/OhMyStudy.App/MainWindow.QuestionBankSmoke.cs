using System.Diagnostics;
using System.Globalization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.ViewModels;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Execution;
using OhMyStudy.Infrastructure.Jobs;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private static Question SmokeQuestion(string key, string number, List<Content> stem, List<Region>? sources = null, List<string>? options = null, double? points = null,
        string answer = "", string explanation = "", Attempt? attempt = null, string subject = "", string questionType = "", List<string>? topics = null) => new()
        {
            DraftKey = key,
            Number = number,
            Subject = subject,
            QuestionType = questionType,
            KnowledgePoints = topics ?? [],
            Stem = stem,
            Sources = sources ?? [],
            Items = [new QuestionItem { Key = key, Options = options ?? [], Points = points, Answer = answer, Explanation = explanation, Attempt = attempt }]
        };

    private static Question WithBody(Question question, List<Content> stem, List<string>? options = null) =>
        question with { Stem = stem, Items = [question.Items[0] with { Options = options ?? [] }] };

    private void ShowQuestionWorkspace()
    {
        HideQuestionPanels();
        SettingsPanel.Visibility = TasksPanel.Visibility = PapersPanel.Visibility = DocumentToolbar.Visibility = SourcePane.Visibility = Visibility.Collapsed;
        WorkspaceActions.Visibility = WorkspaceNotice.Visibility = SectionCaption.Visibility = Visibility.Collapsed;
        WorkspaceGrid.Visibility = WorkspaceStatus.Visibility = Visibility.Visible;
        ViewModel.ShowSection("materials");
        ApplyWorkspaceLayout();
    }

    private async Task RunQuestionBankSmokeAsync(string reportPath)
    {
        string root = Path.Combine(Path.GetDirectoryName(reportPath)!, "bank-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root); _libraryPointerOverride = Path.Combine(root, "active-library.txt");
        var checks = new List<string>(); int exit = 1;
        try
        {
            Require(await InitializeLibraryAsync(Path.Combine(root, "library")), "isolated-library");
            await _jobs!.DisposeAsync();
            _jobs = await JobQueue.CreateAsync(new LibraryJobStore(_library!), new LoggedJobRunner(new BankSmokeRunner(_library!)),
                new PolicyToolExecutor(new HostToolExecutor(root), new SandboxToolExecutor(root)), Path.Combine(root, "task-results"));
            _ = ObserveJobsAsync();
            string providerId = Guid.NewGuid().ToString();
            var provider = new Provider { Id = providerId, Name = "示例供应商", Protocol = Protocol.Responses, BaseUrl = "https://synthetic.invalid/", KeyRef = "OhMyStudy/provider/" + providerId, Models = [] };
            var model = new ModelConfig { Id = "synthetic", Capabilities = Capabilities.Unknown, Currency = "USD" };
            var policy = ExecutionPolicy.Freeze(ExecutionMode.RequestPermission, SandboxCapability.Detect(), false);
            var image = await MakeSyntheticPngAsync();
            var documents = new List<Document>();
            foreach (var (name, subject) in new[] { ("数学 · 函数与导数单元测试", "数学"), ("生物 · 光合作用专题训练", "生物"), ("物理 · 力学综合练习", "物理") })
            {
                var document = await _library!.ImportDocumentAsync(name, ImportMode.AiGrading, new[] { new ImportSource("原件.png", image) });
                await _library.UpdatePaperAsync(document.Id, name, subject, null, null);
                await _jobs.SubmitAsync(DiagnosticKind.Grading, policy, new GradingInput { Document = document, Provider = provider, Model = model, AllowUnknown = true });
                documents.Add(document);
            }
            var clock = Stopwatch.StartNew();
            for (int attempt = 0; attempt < 600 && !((await _jobs.ListAsync()).Count == 3 && (await _jobs.ListAsync()).All(job => job.State == JobState.Completed)); attempt++) await Task.Delay(100);
            Require((await _jobs.ListAsync()).All(job => job.State == JobState.Completed), "seed-jobs-completed");
            checks.Add("seed-ms=" + clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));

            clock.Restart();
            await ShowQuestionLibraryAsync(); await Settle();
            checks.Add("bank-first-page-ms=" + clock.ElapsedMilliseconds.ToString(CultureInfo.InvariantCulture));
            Require(_bankTotal == 3 * BankSmokeRunner.Count && _bankQuestions.Count == BankPageSize && QuestionsPanel.Visibility == Visibility.Visible, $"bank-first-page total={_bankTotal} loaded={_bankQuestions.Count} panel={QuestionsPanel.Visibility} status={ViewModel.StatusMessage}");
            await Capture("bank");
            Require(Previews().Count is > 0 and < 60, "bank-rows-virtualized previews=" + Previews().Count);
            for (int attempt = 0; attempt < 10 && _bankQuestions.Count < _bankTotal; attempt++)
            {
                _bankScroll!.ChangeView(null, _bankScroll.ScrollableHeight, null, true); await Settle();
            }
            Require(_bankQuestions.Count == _bankTotal && _bankQuestions.Select(item => item.Id).Distinct().Count() == _bankTotal, "bank-incremental-loading");
            await Capture("bank-bottom");

            QuestionSearch.Text = "光合作用"; await Settle(700);
            Require(_bankMatching is > 0 && _bankMatching < _bankTotal && BankCount.Text.Contains('/'), "bank-search");
            await Capture("bank-search");
            QuestionSearch.Text = ""; await Settle(700);
            QuestionSubjectFilter.SelectedItem = "物理"; await Settle();
            Require(_bankMatching == BankSmokeRunner.Count && _bankQuestions.All(item => item.Summary.Subject == "物理"), "bank-subject-filter");
            QuestionSubjectFilter.SelectedIndex = 0; await Settle();
            QuestionResultFilter.SelectedIndex = 1; await Settle();
            Require(_bankMatching > 0 && _bankQuestions.All(item => item.Summary.Mistake), "bank-result-filter");
            await Capture("bank-filtered");
            QuestionResultFilter.SelectedIndex = 0; QuestionSort.SelectedIndex = 3; await Settle();
            Require(_bankQuestions[0].Summary.Earned == 0, "bank-lowest-score-first");
            QuestionSort.SelectedIndex = 0; await Settle();
            QuestionSearch.Text = "没有这样的题目"; await Settle(700);
            Require(BankEmpty.Visibility == Visibility.Visible && BankEmptyTitle.Text == UiText.Get("No questions found"), "bank-empty-search");
            await Capture("bank-no-results");
            QuestionSearch.Text = ""; await Settle(700);

            int multiIndex = _bankQuestions.ToList().FindIndex(item => item.Summary.ItemCount > 1);
            await OpenBankQuestionAsync(multiIndex); await Settle();
            var bar = NativeDescendants(QuestionPageHost).OfType<SelectorBar>().SingleOrDefault();
            Require(QuestionReaderPage.Visibility == Visibility.Visible && bar is not null && bar.Items.Count == 3 && ReferenceEquals(bar.SelectedItem, bar.Items[0]), "detail-subquestion-selector");
            Require(NativeDescendants(QuestionPageHost).OfType<Expander>().Count() == 1 && QuestionPosition.Text.Contains('/'), "detail-one-subquestion-at-a-time");
            await Capture("detail-parts");
            bar!.SelectedItem = bar.Items[1]; await Settle();
            Require(_openItems[ViewModel.OpenQuestion.Saved!.Id] == 1 && NativeDescendants(QuestionPageHost).OfType<TextBlock>().Any(text => text.Text == "(2)"), "detail-subquestion-switch");
            await Capture("detail-part-2");
            NativeDescendants(QuestionPageHost).OfType<Expander>().Single().IsExpanded = true; await Settle();
            await Capture("detail-answer");
            string firstTitle = ViewModel.DocumentTitle;
            NextQuestion_Click(this, new RoutedEventArgs()); await Settle();
            Require(ViewModel.DocumentTitle != firstTitle && _openIndex == multiIndex + 1, "detail-next");
            await Capture("detail-single");
            PreviousQuestion_Click(this, new RoutedEventArgs()); await Settle();
            Require(ViewModel.DocumentTitle == firstTitle && NativeDescendants(QuestionPageHost).OfType<SelectorBar>().Single().SelectedItem is { } remembered
                && NativeDescendants(QuestionPageHost).OfType<SelectorBar>().Single().Items.IndexOf(remembered) == 1, "detail-previous-remembers-subquestion");
            ViewModel.OpenQuestion.Notes = "复习：先写定义域，再讨论单调性。"; await Settle();
            Require(ViewModel.HasUnsavedChanges, "notes-dirty");
            await Capture("detail-notes");
            await ViewModel.SaveAsync(ViewModel.OpenQuestion);
            Require(!ViewModel.OpenQuestion.IsDirty && (await _library!.GetQuestionAsync(ViewModel.OpenQuestion.Saved!.Id)).Question.Notes == "复习：先写定义域，再讨论单调性。", "notes-saved");
            await ReturnToBankAsync(reload: false); await Settle();
            Require(QuestionsPanel.Visibility == Visibility.Visible && QuestionReaderPage.Visibility == Visibility.Collapsed && BankList.ContainerFromIndex(multiIndex) is not null, "back-restores-list-position");

            Navigation.SelectedItem = Navigation.MenuItems[2]; await Settle();
            Require(_bankTotal == _bankMatching && _bankTotal == 3 * BankSmokeRunner.Count / 2 && _bankQuestions.All(item => item.Summary.Mistake) && QuestionResultFilter.Visibility == Visibility.Collapsed, "mistakes-page");
            await Capture("mistakes");
            int mistakeMulti = _bankQuestions.ToList().FindIndex(item => item.Summary.ItemCount > 1);
            await OpenBankQuestionAsync(mistakeMulti); await Settle();
            var mistakeBar = NativeDescendants(QuestionPageHost).OfType<SelectorBar>().Single();
            Require(mistakeBar.Items.IndexOf(mistakeBar.SelectedItem) == 1 && QuestionBackLabel.Text == UiText.Get("Mistakes"), "mistakes-open-first-wrong-subquestion");
            await Capture("mistakes-detail");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Settle(); await Capture("mistakes-detail-dark");
            await ReturnToBankAsync(reload: false); await Settle(); await Capture("mistakes-dark");
            RootGrid.RequestedTheme = ElementTheme.Light;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 900)); await Settle(); await Capture("mistakes-medium");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 900)); await Settle(); await Capture("mistakes-compact");
            await OpenBankQuestionAsync(mistakeMulti); await Settle(); await Capture("detail-compact");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 900)); await Settle(); await Capture("detail-medium");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960)); await Settle();

            var opened = ViewModel.OpenQuestion.Saved!;
            ViewInExam_Click(this, new RoutedEventArgs());
            for (int attempt = 0; attempt < 40 && SourcePane.Visibility != Visibility.Visible; attempt++) await Task.Delay(100);
            await Settle();
            Require(WorkspaceGrid.Visibility == Visibility.Visible && SourcePane.Visibility == Visibility.Visible && ViewModel.CurrentDocument?.Id == opened.DocumentId
                && ViewModel.SelectedQuestion.Saved?.Id == opened.Id && QuestionReaderPage.Visibility == Visibility.Collapsed, "view-in-exam");
            await Capture("view-in-exam");
            CloseOriginal_Click(this, new RoutedEventArgs()); await Settle(); await Capture("exam-detail");
            exit = 0;
        }
        catch (Exception error) { AppLog.Error("QuestionBankSmoke", error); checks.Add("FAIL " + error); }
        finally
        {
            await File.WriteAllLinesAsync(reportPath, checks.Prepend(exit == 0 ? "PASS" : "FAIL"));
            if (_jobs is not null) await _jobs.DisposeAsync(); _jobs = null;
            if (_library is not null) await _library.DisposeAsync(); _library = null;
            _shutdownFinished = true; Microsoft.UI.Xaml.Application.Current.Exit(); Environment.Exit(exit);
        }

        void Require(bool value, string label) { if (!value) throw new InvalidOperationException(label); checks.Add(label); }
        List<QuestionPreview> Previews() => NativeDescendants(BankList).OfType<QuestionPreview>().Where(preview => preview.IsLoaded).ToList();
        async Task Settle(int delay = 300)
        {
            await Task.Delay(delay); RootGrid.UpdateLayout();
            await _openPresentationTask; await _questionPresentationTask;
            for (int attempt = 0; attempt < 30; attempt++)
            {
                if (!_bankLoading && Previews().All(preview => preview.Ready.IsCompleted)) break;
                await Task.Delay(100); RootGrid.UpdateLayout();
            }
            await Task.Delay(120);
        }
        async Task Capture(string name) => await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + ".png"));
    }

    private sealed class BankSmokeRunner(LibraryRepository library) : IJobRunner
    {
        public const int Count = 120;

        public async Task RunAsync(Job job, JobContext context)
        {
            await context.CommitAsync(token => library.BeginGradingAsync(job, token));
            var document = job.Input!.Document;
            var region = new Region { PageId = document.Pages[0].Id, X = .1, Y = .1, Width = .8, Height = .3 };
            string subject = document.Name[..2];
            for (int index = 1; index <= Count; index++)
            {
                string key = "bank-" + index.ToString(CultureInfo.InvariantCulture);
                var question = (index % 4) switch
                {
                    1 => Multi(key, index, subject, region),
                    2 => Single(key, index, subject, region, Verdict.Correct, 4, "x = 2", "代入验证得 x = 2。"),
                    3 => Single(key, index, subject, region, Verdict.Incorrect, 0, "x = −2", "符号写反，应为 x = 2。"),
                    _ => Single(key, index, subject, region, Verdict.Uncertain, null, "字迹模糊", "作答无法完全辨认。") with { Uncertainties = ["第二行作答字迹模糊，请核对原卷。"] }
                };
                await context.CommitAsync(token => library.SaveGradingQuestionAsync(job.Id, question, token));
            }
            await context.ProgressAsync(90, $"已保存 {Count} 题。");
        }

        private static List<Content> Stem(string subject, int index) => subject switch
        {
            "数学" => [new TextContent { Text = "已知函数 " }, new FormulaContent { Latex = @"f(x)=x^3-3x+" + index.ToString(CultureInfo.InvariantCulture) }, new TextContent { Text = "，讨论函数在区间 [−2, 2] 上的单调性与最值。" }],
            "生物" => [new TextContent { Text = "当光照过强，植物吸收的光能超过光合作用所需时，会导致光合速率下降，这种现象称为光抑制。阅读材料并回答问题（第 " + index.ToString(CultureInfo.InvariantCulture) + " 组）。" }],
            _ => [new TextContent { Text = "质量为 m 的物块在倾角为 θ 的斜面上由静止释放，" }, new FormulaContent { Latex = @"\mu=0.2,\ g=10\,\mathrm{m/s^2}" }, new TextContent { Text = "，求物块的加速度。" }]
        };

        private static Question Single(string key, int index, string subject, Region region, Verdict verdict, double? earned, string response, string reason) => new()
        {
            DraftKey = key,
            Number = index.ToString(CultureInfo.InvariantCulture),
            Subject = subject,
            QuestionType = "解答题",
            Stem = Stem(subject, index),
            Sources = [region],
            KnowledgePoints = [subject == "数学" ? "导数与单调性" : subject == "生物" ? "光合作用" : "牛顿第二定律"],
            Items = [new QuestionItem
            {
                Key = key,
                Points = 4,
                Answer = "x = 2",
                Explanation = "由题意列方程并求解，注意检验。",
                Attempt = new Attempt { StudentAnswer = response, StudentRegions = [region], AiSolution = "先列式，再化简求解。", Grade = new Grade { Verdict = verdict, Earned = earned, Possible = verdict == Verdict.Uncertain ? null : 4, Source = GradeSource.AiSolution, Reason = reason } }
            }]
        };

        private static Question Multi(string key, int index, string subject, Region region)
        {
            QuestionItem Item(int part, string prompt, Verdict verdict, double earned, double points, string response) => new()
            {
                Key = key + "-" + part.ToString(CultureInfo.InvariantCulture),
                Label = "(" + part.ToString(CultureInfo.InvariantCulture) + ")",
                Prompt = [new TextContent { Text = prompt }],
                Points = points,
                Answer = part == 2 ? @"\(f'(x)=3x^2-3\)" : "类囊体薄膜",
                Explanation = "结合材料中的关键信息作答。",
                Attempt = new Attempt { StudentAnswer = response, StudentRegions = [region], AiSolution = "按步骤作答。", Grade = new Grade { Verdict = verdict, Earned = earned, Possible = points, Source = GradeSource.AiSolution, Reason = verdict == Verdict.Correct ? "回答正确。" : "关键步骤缺失，扣分。" } }
            };
            return new()
            {
                DraftKey = key,
                Number = index.ToString(CultureInfo.InvariantCulture),
                Subject = subject,
                QuestionType = "综合题",
                Stem = Stem(subject, index),
                Sources = [region],
                KnowledgePoints = ["综合分析"],
                Items = [Item(1, "PSⅡ 位于什么结构上？", Verdict.Correct, 2, 2, "类囊体薄膜"), Item(2, "写出关键关系式并说明理由。", Verdict.Incorrect, 0, 3, "不会"), Item(3, "根据材料推断并说明原因。", Verdict.Partial, 2, 5, "光照过强导致电子积累")]
            };
        }
    }
}
