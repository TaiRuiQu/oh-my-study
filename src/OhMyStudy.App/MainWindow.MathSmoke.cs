using System.Diagnostics;
using Microsoft.UI.Xaml;
using OhMyStudy.App.Services;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.Domain;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    // Isolated actual-XAML typography and switching benchmark. No real library or credentials.
    private async Task RunMathSmokeAsync(string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string root = Path.Combine(Path.GetDirectoryName(reportPath)!, "math-" + Guid.NewGuid().ToString("N"));
        _libraryPointerOverride = Path.Combine(root, "active-library.txt");
        var evidence = new List<string>();
        int exitCode = 1;
        try
        {
            await InitializeLibraryAsync(Path.Combine(root, "library"));
            await ShowQuestionLibraryAsync();
            await Capture("empty");
            var question = new Question
            {
                DraftKey = "math",
                Number = "1",
                Subject = "数学",
                QuestionType = "选择",
                Points = 5,
                Content = [new TextContent { Text = "已知集合" },
                    new FormulaContent { Latex = @"A=\{0,1,2,3,4,5\},\ B=\{x\mid x^{2}-7x+6\le 0\}" },
                    new TextContent { Text = @"，则 $A\cap B=$。" },
                    new TextContent { Text = "\n下列各式用于检查分式、根式和上下标：\n" + @"$\dfrac{x^2-1}{x-1}+\dfrac{1}{\sqrt{x+2}}$，\(S_n=\sum_{k=1}^{n}k^2\)。" },
                    new TextContent { Text = @"\[f(x)=\begin{cases}x^2&x\ge0\\-x&x<0\end{cases}\]" }],
                Options = [@"A. \([1,5]\)", @"B. $[0,6]$", @"C. $\{2,3,4,5\}$", @"D. \(\{1,2,3,4,5\}\)"],
                StandardAnswer = "D",
                Explanation = "",
                KnowledgePoints = [],
                Sources = [],
                Uncertainties = [],
                Parts = [],
                Attempt = null
            };
            // A saved item makes the normal detail pane and controls visible.
            if (_library is null) throw new InvalidOperationException("Isolated library unavailable");
            var saved = new SavedQuestion { Id = "math", DocumentId = "math", UpdatedAt = "1", Question = question };

            foreach (string name in new[] { "cold", "warm", "repeat" })
            {
                var clock = Stopwatch.StartNew();
                var body = await NativeQuestionPresenter.CreateAsync(question, _library, _lifetime.Token);
                evidence.Add(name + "-ms=" + clock.Elapsed.TotalMilliseconds.ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
                if (name == "cold") ViewModel.LoadLibrary(new[] { saved }, false);
                await Capture(name);
            }
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("dark");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 960)); await Capture("narrow-dark");
            RootGrid.RequestedTheme = ElementTheme.Light;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("compact");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            var vector = question with { Number = "2", Content = [new TextContent { Text = @"向量 \(\boldsymbol{a}=(0,1),\ \boldsymbol{b}=(1,2)\)，求 \(\boldsymbol{a}\cdot\boldsymbol{b}\)。" }, new TextContent { Text = "\n" + @"无效公式保留原文：$\unsupported{x}$；未闭合：\(x。" }], Options = [] };
            Show(vector); await Capture("errors");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("errors-dark");
            RootGrid.RequestedTheme = ElementTheme.Light;
            var table = question with { Content = [new TextContent { Text = "根据表格中的函数，完成下面的小题。" }, new TableContent { Rows = [["函数", "导数"], [@"\(f(x)=x^2\)", @"$f'(x)=2x$"], [@"\(g(x)=\sqrt{x}\)", @"$g'(x)=\dfrac{1}{2\sqrt{x}}$"]] }], Options = [], Parts = [vector with { Number = "(1)", Content = [new TextContent { Text = @"求 \(\dfrac{d}{dx}(x^3)\)，并说明过程。" }] }] };
            Show(table); await Capture("table-parts");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("table-compact");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            var longQuestion = question with { Content = [new TextContent { Text = string.Concat(Enumerable.Repeat(@"文字应随窗口自然换行，\(x^2+y^2=1\) 与正文保持一致。", 6)) + "\n" }, new FormulaContent { Latex = "$$" + string.Join("+", Enumerable.Range(1, 32).Select(index => @"\dfrac{x_{" + index + @"}^2}{" + index + "}")) + "$$" }], Options = [] };
            Show(longQuestion); await Capture("long");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("long-compact");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            Show(question with { Content = [new TextContent { Text = "不含公式的普通题目，文字仍可选择和复制。" }], Options = [] }); await Capture("plain");
            var other = saved with { Id = "other", Question = vector, UpdatedAt = "2" };
            ViewModel.LoadLibrary(new[] { saved, other }, false);
            await Capture("multiple");
            QuestionList.SelectedIndex = 1; await Task.Delay(250); await _questionPresentationTask;
            var switches = new List<double>();
            for (int index = 0; index < 12; index++)
            {
                var clock = Stopwatch.StartNew();
                QuestionList.SelectedIndex = index % 2;
                await _questionPresentationTask;
                RootGrid.UpdateLayout();
                switches.Add(clock.Elapsed.TotalMilliseconds);
            }
            switches.Sort();
            evidence.Add("selection-warm-p95-ms=" + switches[(int)Math.Ceiling(switches.Count * .95) - 1].ToString("F2", System.Globalization.CultureInfo.InvariantCulture));
            for (int index = 0; index < 12; index++) QuestionList.SelectedIndex = index % 2;
            await _questionPresentationTask;
            if (ViewModel.SelectedQuestion.Saved?.Id != "other" || QuestionContentHost.Content is not Microsoft.UI.Xaml.Controls.StackPanel)
                throw new InvalidOperationException("Latest selected question was not presented");
            evidence.Add("rapid-switch-keeps-latest=PASS");
            await Capture("rapid-switch");

            void Show(Question current)
            {
                ViewModel.LoadLibrary(new[] { saved with { Question = current, UpdatedAt = Guid.NewGuid().ToString() } }, false);
            }
            exitCode = 0;
        }
        catch (Exception error) { evidence.Add(error.ToString()); AppLog.Error("MathSmoke", error); }
        finally
        {
            await File.WriteAllLinesAsync(reportPath, evidence.Prepend(exitCode == 0 ? "PASS" : "FAIL"));
            _shutdownFinished = true;
            Microsoft.UI.Xaml.Application.Current.Exit();
            Environment.Exit(exitCode);
        }
        async Task Capture(string name)
        {
            int stable = 0;
            for (int attempt = 0; attempt < 20 && stable < 2; attempt++)
            {
                await Task.Delay(100); RootGrid.UpdateLayout();
                long generation = _questionPresentationGeneration;
                await _questionPresentationTask;
                stable = generation == _questionPresentationGeneration && (ViewModel.Questions.Count == 0
                    || Math.Abs(QuestionContentHost.ActualWidth - _questionPresentationWidth) < 2) ? stable + 1 : 0;
            }
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + ".png"));
        }
    }
}
