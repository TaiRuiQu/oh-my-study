using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Automation;
using OhMyStudy.App.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.Services;
using VelociTex.WinUI;
using OhMyStudy.Domain;
using OhMyStudy.Documents;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task RunPreviewSmokeAsync(string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string root = Path.Combine(Path.GetDirectoryName(reportPath)!, "previews-" + Guid.NewGuid().ToString("N"));
        _libraryPointerOverride = Path.Combine(root, "active-library.txt");
        var checks = new List<string>();
        int exitCode = 1;
        try
        {
            await InitializeLibraryAsync(Path.Combine(root, "library"));
            await ShowQuestionLibraryAsync();
            var template = new Question
            {
                DraftKey = "preview",
                Number = "1",
                Subject = "数学",
                QuestionType = "选择",
                Points = 5,
                Content = [],
                Options = [],
                StandardAnswer = "",
                Explanation = "",
                KnowledgePoints = [],
                Sources = [],
                Uncertainties = [],
                Parts = [],
                Attempt = null
            };
            var bodies = new List<Content>[] {
                [new TextContent { Text = "已知集合" }, new FormulaContent { Latex = @"A=\{0,1,2,3,4,5\},\ B=\{x\mid x^2-7x+6\le0\}" }, new TextContent { Text = @"，则 \(A\cap B=\)" }],
                [new TextContent { Text = "已知向量" }, new FormulaContent { Latex = @"\boldsymbol a=(0,1),\ \boldsymbol b=(1,2)" }, new TextContent { Text = @"，求 \(\boldsymbol a\cdot\boldsymbol b\)。" }],
                [new TextContent { Text = "记" }, new FormulaContent { Latex = @"S_n" }, new TextContent { Text = @"为数列前 n 项和，求 \(S_n=\sum_{k=1}^{n}k^2\)。" }],
                [new TextContent { Text = "已知集合" }, new FormulaContent { Latex = @"A=\{x\mid e^{\ln x}<1\},\ B=\{x\mid\ln e^x<1\}" }, new TextContent { Text = @"，则“\(x\in A\)”是“\(x\in B\)”的" }],
                [new TextContent { Text = "已知 " }, new FormulaContent { Latex = @"F_1,F_2" }, new TextContent { Text = "分别为椭圆的两个焦点。" }],
                [new FormulaContent { Latex = @"(a+2x)(1+x)^{4}" }, new TextContent { Text = "的展开式中，求指定项的系数。" }],
                [new TextContent { Text = "已知函数 " }, new FormulaContent { Latex = @"f(x)=\dfrac{1}{e^{x}+1}+x" }, new TextContent { Text = "，求函数的单调区间。" }],
                [new TextContent { Text = "如图所示的六面体 " }, new FormulaContent { Latex = "ABCDEF" }],
                [new TextContent { Text = @"计算积分 \(\int_{0}^{1}x^2\,dx\)，说明求解过程。" }],
                [new TextContent { Text = string.Concat(Enumerable.Repeat("长文字需要正确换行，预览只展示开头的内容。", 10)) }],
                [],
                [new TextContent { Text = @"暂不支持的表达式：$\unsupported{x}$，原文应在详情保留。" }],
                [new TextContent { Text = @"已知矩阵 \(A=\begin{pmatrix}1&2\\3&4\end{pmatrix}\)，求 \(A^2\)。" }],
                [new TextContent { Text = @"分段函数 \[f(x)=\begin{cases}x^2&x\ge0\\-x&x<0\end{cases}\]，求函数的值。" }],
                [new TextContent { Text = @"由 \(\text{平均速度}=\frac{\text{总路程}}{\text{总时间}}\) 求解。" }],
                [new TextContent { Text = "比较以下选项中的表达式，并选择正确答案。" }],
                [new FormulaContent { Latex = "$$" + string.Join("+", Enumerable.Range(1, 24).Select(index => @"\dfrac{x_{" + index + @"}^2}{" + index + "}")) + "$$" }],
                [new TableContent { Rows = [["函数", "导数"], [@"$x^2$", @"$2x$"]] }],
                [new TextContent { Text = @"已知 \(\alpha+\beta=\pi\)，求 \(\sin\alpha+\cos\beta\)。" }]
            };
            var items = bodies.Select((body, index) => Save(template with
            {
                Number = (index + 1).ToString(),
                Content = body,
                Options = index == 3 ? ["A. 充分不必要条件", "B. 必要不充分条件", "C. 既不充分也不必要条件", "D. 充要条件"]
                    : index == 15 ? [@"A. \dfrac{1}{2}", @"B. $\sqrt{2}$", @"C. 当 x>0 时，\frac{1}{x} 为正", @"D. \(x^2+1\)"]
                    : ["A. [1,5]", "B. [0,6]", @"C. $\{2,3,4,5\}$", @"D. \(\{1,2,3,4,5\}\)"]
            }, index)).ToArray();
            var clock = Stopwatch.StartNew();
            ViewModel.LoadLibrary(items, false);
            await Capture("list"); checks.Add($"first-scene-with-screenshot-ms={clock.Elapsed.TotalMilliseconds:F2}");
            Check(VisiblePreviews().All(preview => PreviewRendered(preview)), "visible-previews-use-vector-content");
            var firstPreview = VisiblePreviews().First();
            var hitPoint = firstPreview.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(firstPreview.ActualWidth / 2, firstPreview.ActualHeight / 2));
            var hits = VisualTreeHelper.FindElementsInHostCoordinates(hitPoint, RootGrid).ToArray();
            Check(hits.Any(element => IsWithin(element, (DependencyObject)QuestionList.ContainerFromItem(firstPreview.Item)))
                && hits.All(element => element is not ScrollViewer || !IsWithin(element, firstPreview)), "preview-hit-target-remains-the-list-item");
            QuestionList.SelectedIndex = 3; await Capture("plain-options");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("dark");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(900, 850)); await Capture("medium-dark");
            AppWindow.Resize(new Windows.Graphics.SizeInt32(720, 960)); await Capture("narrow-dark");
            RootGrid.RequestedTheme = ElementTheme.Light;
            AppWindow.Resize(new Windows.Graphics.SizeInt32(460, 960)); await Capture("compact");
            if (QuestionList.ContainerFromItem(ViewModel.SelectedQuestion) is FrameworkElement selected)
            {
                double y = selected.TransformToVisual(QuestionList).TransformPoint(new Windows.Foundation.Point()).Y;
                Check(y >= -1 && y + selected.ActualHeight <= QuestionList.ActualHeight + 1, "selected-row-visible-after-responsive-layout");
            }
            AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
            QuestionList.SelectedIndex = 15; QuestionList.ScrollIntoView(ViewModel.Questions[15], ScrollIntoViewAlignment.Leading); await Capture("math-options");
            QuestionList.SelectedIndex = 12; await Capture("matrix");
            QuestionList.SelectedIndex = 11; await Capture("unsupported");
            QuestionList.ScrollIntoView(ViewModel.Questions[^1], ScrollIntoViewAlignment.Leading); await Capture("bottom");
            RootGrid.RequestedTheme = ElementTheme.Dark; await Capture("bottom-dark"); RootGrid.RequestedTheme = ElementTheme.Light;
            ViewModel.LoadLibrary(new[] { items[16] }, false); await Capture("single-formula");
            await ShowQuestionLibraryAsync(); await Capture("empty");
            var many = Enumerable.Range(0, 500).Select(index => Save(template with
            {
                Number = (index + 1).ToString(),
                Content = [new TextContent { Text = "已知第 " + (index + 1) + " 组函数 " }, new FormulaContent { Latex = "f_{" + (index + 1) + "}(x)=x^2+" + (index + 1) }, new TextContent { Text = "，求函数的值。" }]
            }, index)).ToArray();
            ViewModel.LoadLibrary(many, false); await Capture("large-top");
            Check(Realized().Count is > 0 and < 100, "five-hundred-rows-remain-virtualized");
            QuestionList.ScrollIntoView(ViewModel.Questions[250], ScrollIntoViewAlignment.Leading); await Capture("large-middle");
            QuestionList.ScrollIntoView(ViewModel.Questions[^1], ScrollIntoViewAlignment.Leading); await Capture("large-bottom");
            for (int index = 0; index < 12; index++) QuestionList.ScrollIntoView(ViewModel.Questions[index % 2 == 0 ? 0 : 499], ScrollIntoViewAlignment.Leading);
            await Capture("recycled");
            var buffered = Realized().Where(preview => !PreviewRendered(preview)).ToArray();
            checks.Add("unpainted-buffered-rows=" + buffered.Length);
            foreach (var preview in buffered)
                checks.Add($"buffer-row={preview.Item?.Number};width={preview.ActualWidth:F1};y={preview.TransformToVisual(QuestionList).TransformPoint(new Windows.Foundation.Point()).Y:F1};task={preview.Ready.Status}");
            Check(VisiblePreviews().All(preview => preview.Item is not null && PreviewRendered(preview)), "recycled-rows-show-current-vector-previews");
            Check(VisiblePreviews().All(preview => preview.RenderedSource == preview.Item!.PreviewText), "recycled-preview-content-matches-current-item");
            var delays = new List<double>();
            for (int index = 0; index < 12; index++)
            {
                var sent = Stopwatch.StartNew(); var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                DispatcherQueue.TryEnqueue(() => ready.TrySetResult()); await ready.Task; delays.Add(sent.Elapsed.TotalMilliseconds);
            }
            checks.Add($"dispatcher-max-ms={delays.Max():F2}");
            await RunNativeFormulaAcceptanceAsync(checks);
            exitCode = 0;
        }
        catch (Exception error) { checks.Add(error.ToString()); AppLog.Error("PreviewSmoke", error); }
        finally
        {
            await File.WriteAllLinesAsync(reportPath, checks.Prepend(exitCode == 0 ? "PASS" : "FAIL"));
            _shutdownFinished = true; Microsoft.UI.Xaml.Application.Current.Exit(); Environment.Exit(exitCode);
        }

        SavedQuestion Save(Question question, int index) => new() { Id = "preview-" + index, DocumentId = "preview", UpdatedAt = "1", Question = question };
        void Check(bool condition, string name) { if (!condition) throw new InvalidOperationException(name); checks.Add(name + "=PASS"); }
        bool PreviewRendered(QuestionPreview preview) => preview.Ready.IsCompletedSuccessfully && preview.RenderedSource == preview.Item?.PreviewText && preview.Children.Count == 1;
        List<QuestionPreview> Realized() => Descendants(QuestionList).OfType<QuestionPreview>().Where(preview => preview.IsLoaded).ToList();
        List<QuestionPreview> VisiblePreviews() => Realized().Where(preview =>
        {
            double y = preview.TransformToVisual(QuestionList).TransformPoint(new Windows.Foundation.Point()).Y;
            return preview.Item is not null && preview.ActualWidth >= 64 && y + preview.ActualHeight > 0 && y < QuestionList.ActualHeight;
        }).ToList();
        IEnumerable<DependencyObject> Descendants(DependencyObject parent)
        {
            for (int index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
            {
                var child = VisualTreeHelper.GetChild(parent, index); yield return child;
                foreach (var descendant in Descendants(child)) yield return descendant;
            }
        }
        async Task Capture(string name)
        {
            int stable = 0;
            for (int attempt = 0; attempt < 60 && stable < 2; attempt++)
            {
                await Task.Delay(100); RootGrid.UpdateLayout();
                await _questionPresentationTask;
                var before = VisiblePreviews();
                var tasks = before.Select(preview => preview.Ready).ToArray();
                await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
                RootGrid.UpdateLayout();
                var current = VisiblePreviews();
                bool ready = (ViewModel.Questions.Count == 0 || current.Count > 0)
                    && before.SequenceEqual(current) && tasks.SequenceEqual(current.Select(preview => preview.Ready))
                    && current.All(preview => PreviewRendered(preview));
                stable = ready ? stable + 1 : 0;
            }
            if (stable < 2) throw new InvalidOperationException("Visible previews did not reach a stable rendered state: " + name);
            await CaptureWorkspaceAsync(Path.ChangeExtension(reportPath, "." + name + ".png"));
            if (RootGrid.ActualTheme == ElementTheme.Dark)
            {
                var images = Descendants(QuestionContentHost).OfType<FrameworkElement>().Where(element => element is NativeFormulaParagraph or MathView).ToArray();
                if (images.Length == 0) throw new InvalidOperationException("Dark reading scene has no rendered images: " + name);
                var screenshot = await Windows.Storage.StorageFile.GetFileFromPathAsync(Path.ChangeExtension(reportPath, "." + name + ".png"));
                using var stream = await screenshot.OpenReadAsync();
                var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                var pixels = (await decoder.GetPixelDataAsync(Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
                    Windows.Graphics.Imaging.BitmapAlphaMode.Straight, new Windows.Graphics.Imaging.BitmapTransform(),
                    Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation, Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage)).DetachPixelData();
                double scaleX = decoder.PixelWidth / RootGrid.ActualWidth, scaleY = decoder.PixelHeight / RootGrid.ActualHeight;
                foreach (var image in images)
                {
                    var origin = image.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point());
                    int left = Math.Clamp((int)Math.Floor(origin.X * scaleX), 0, (int)decoder.PixelWidth);
                    int top = Math.Clamp((int)Math.Floor(origin.Y * scaleY), 0, (int)decoder.PixelHeight);
                    int right = Math.Clamp((int)Math.Ceiling((origin.X + image.ActualWidth) * scaleX), 0, (int)decoder.PixelWidth);
                    int bottom = Math.Clamp((int)Math.Ceiling((origin.Y + image.ActualHeight) * scaleY), 0, (int)decoder.PixelHeight);
                    bool lightInk = false;
                    for (int y = top; y < bottom && !lightInk; y++)
                        for (int x = left; x < right; x++)
                        {
                            int index = (y * (int)decoder.PixelWidth + x) * 4;
                            if (pixels[index] > 160 && pixels[index + 1] > 160 && pixels[index + 2] > 160) { lightInk = true; break; }
                        }
                    if (!lightInk) throw new InvalidOperationException("Dark reading image has no contrasting ink: " + name);
                }
                checks.Add(name + "-rendered-reading-contrast=PASS");
            }
        }
    }
}
