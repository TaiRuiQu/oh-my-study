using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using OhMyStudy.App.Services;
using OhMyStudy.Documents;
using OhMyStudy.Domain;
using Windows.Foundation;
using Windows.Graphics.Printing;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using UiImage = Microsoft.UI.Xaml.Controls.Image;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private bool _exportingQuestions;
    private bool _editingPages;
    private bool _preparingPrint;
    private OriginalPrintSession? _originalPrintSession;

    private async void ExportQuestions_Click(object sender, RoutedEventArgs e)
    {
        if (_exportingQuestions || _closed) return;
        var saved = ViewModel.Questions.Where(item => item.Saved is not null).Select(item => item.Saved!).ToList();
        string title = ViewModel.CurrentDocument?.Name ?? "题目";
        if (saved.Count == 0) { ViewModel.StatusMessage = "请先整理并保存题目。"; return; }
        if (saved.Any(item => !CanExportQuestion(item.Question)))
        { ViewModel.StatusMessage = "部分题目含图片、表格、多层小问或暂不支持的公式，暂不能完整导出。请保留原资料。"; return; }
        _exportingQuestions = true;
        try
        {
            var paper = new ComboBox { Header = "纸张", SelectedIndex = 0, Items = { "A4", "A3 横向" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            var edition = new ComboBox { Header = "内容", SelectedIndex = 0, Items = { "题目", "答案与解析" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = $"导出当前显示的 {saved.Count} 题。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(paper); content.Children.Add(edition);
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "导出题目", Content = content, PrimaryButtonText = "继续", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = "题目" };
            picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            var document = new ExamDocument(title, saved.Select((item, index) => ExportQuestion(item.Question, index + 1)).ToList());
            var size = paper.SelectedIndex == 1 ? ExamPaper.A3Landscape : ExamPaper.A4Portrait;
            var version = edition.SelectedIndex == 1 ? ExamEdition.Answers : ExamEdition.Questions;
            ViewModel.StatusMessage = "正在导出…";
            var bytes = await Task.Run(async () =>
            {
                var service = new TypstDocumentService(new(Path.Combine(AppContext.BaseDirectory, "native", "typst", "typst.exe"), Path.Combine(AppContext.BaseDirectory, "native", "fonts")));
                var directory = Directory.CreateTempSubdirectory("OhMyStudy-Export-").FullName;
                try
                {
                    var result = await service.ExportExamAsync(document, size, version, directory, _lifetime.Token).ConfigureAwait(false);
                    if (result.Diagnostics.Count != 0) throw new InvalidDataException("Incomplete export");
                    return await File.ReadAllBytesAsync(result.PdfPath, _lifetime.Token).ConfigureAwait(false);
                }
                finally { DeleteExportFiles(directory); }
            }, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            // The picker owns the destination; this is the first write of the complete verified PDF.
            await FileIO.WriteBytesAsync(file, bytes).AsTask(_lifetime.Token);
            ViewModel.StatusMessage = "PDF 已保存。";
        }
        catch (OperationCanceledException) { if (!_closed) ViewModel.StatusMessage = "已取消导出。"; }
        catch { if (!_closed) ViewModel.StatusMessage = "导出未完成，请检查保存位置或重新安装应用后重试。"; }
        finally { _exportingQuestions = false; }
    }

    private static bool CanExportQuestion(Question question) => question.Content.All(CanExportContent)
        && question.Parts.All(part => part.Parts.Count == 0 && part.Content.All(CanExportContent));
    private static bool CanExportContent(Content content) => content is TextContent || content is FormulaContent formula && LatexFormulaConverter.Convert(formula.Latex).IsSupported;
    private static List<ExamSegment> ExportSegments(Question question) => question.Content.Select(content => content switch
    { TextContent text => new ExamSegment(text.Text), FormulaContent formula => new ExamSegment(formula.Latex, true), _ => throw new InvalidOperationException() }).ToList();
    private static ExamQuestion ExportQuestion(Question question, int ordinal)
    {
        var body = ExportSegments(question);
        body.Insert(0, new ExamSegment($"原题号：{question.Number}"));
        var parts = question.Parts.Select(part => new ExamPart(part.Number, ExportSegments(part).Concat(part.Options.Select(option => new ExamSegment(option))).ToList(), string.IsNullOrWhiteSpace(part.Explanation) ? part.StandardAnswer : part.StandardAnswer + "\n解析：" + part.Explanation)).ToList();
        return new ExamQuestion(ordinal, body, question.StandardAnswer, question.Explanation, Options: question.Options, Parts: parts);
    }
    private static void DeleteExportFiles(string directory)
    {
        try
        {
            foreach (var name in new[] { "a4-questions", "a4-answers", "a3-questions", "a3-answers" })
                foreach (var extension in new[] { ".typ", ".pdf" }) File.Delete(Path.Combine(directory, name + extension));
            var packages = Path.Combine(directory, "empty-packages");
            if (Directory.Exists(packages)) Directory.Delete(packages, recursive: false);
            Directory.Delete(directory, recursive: false);
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private async void ManagePages_Click(object sender, RoutedEventArgs e)
    {
        if (_editingPages || _library is null || ViewModel.CurrentDocument is not { } selected || ViewModel.IsSample) return;
        _editingPages = true;
        try
        {
            var document = await _library.GetDocumentAsync(selected.Id, _lifetime.Token);
            var grading = await _library.GradingResultAsync(document.Id, _lifetime.Token);
            var protectedPages = grading.Questions.SelectMany(saved => QuestionPageIds(saved.Question))
                .Concat(grading.References.SelectMany(reference => reference.Sources.Select(region => region.PageId))).ToHashSet(StringComparer.Ordinal);
            var pages = document.Pages.Select(page => page with { }).ToList();
            var rows = new StackPanel { Spacing = 8, MinWidth = 480 };
            var notice = new TextBlock { Text = "已有题目或参考答案引用的页面不能移除。", TextWrapping = TextWrapping.Wrap };
            void Refresh()
            {
                rows.Children.Clear(); rows.Children.Add(notice);
                for (int index = 0; index < pages.Count; index++)
                {
                    int current = index;
                    var page = pages[index];
                    var line = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    line.Children.Add(new TextBlock { Text = $"第 {index + 1} 页　{(page.Role == "reference" ? "参考答案" : "材料")}　{page.Rotation}°", Width = 180, VerticalAlignment = VerticalAlignment.Center });
                    var up = new Button { Content = "上移", IsEnabled = index > 0 };
                    up.Click += (_, _) => { (pages[current - 1], pages[current]) = (pages[current], pages[current - 1]); Refresh(); };
                    var down = new Button { Content = "下移", IsEnabled = index < pages.Count - 1 };
                    down.Click += (_, _) => { (pages[current + 1], pages[current]) = (pages[current], pages[current + 1]); Refresh(); };
                    var rotateLeft = new Button { Content = "左转" };
                    rotateLeft.Click += (_, _) => { pages[current] = pages[current] with { Rotation = (ushort)((pages[current].Rotation + 270) % 360) }; Refresh(); };
                    var rotateRight = new Button { Content = "右转" };
                    rotateRight.Click += (_, _) => { pages[current] = pages[current] with { Rotation = (ushort)((pages[current].Rotation + 90) % 360) }; Refresh(); };
                    var remove = new Button { Content = "移除", IsEnabled = pages.Count > 1 && !protectedPages.Contains(page.Id) };
                    if (protectedPages.Contains(page.Id)) ToolTipService.SetToolTip(remove, "此页仍被题目或参考答案引用。");
                    remove.Click += (_, _) => { if (pages.Count > 1) { pages.RemoveAt(current); Refresh(); } };
                    line.Children.Add(up); line.Children.Add(down); line.Children.Add(rotateLeft); line.Children.Add(rotateRight); line.Children.Add(remove);
                    rows.Children.Add(line);
                }
            }
            Refresh();
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "整理页面", Content = new ScrollViewer { Content = rows, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = "保存", CloseButtonText = "取消", DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await _library.UpdatePagesAsync(document.Id, pages.Select(page => new PageEdit { Id = page.Id, Rotation = page.Rotation }).ToList(), _lifetime.Token);
            foreach (var page in document.Pages) _pageCache.Invalidate(page.Id);
            await RefreshDocumentsAsync(document.Id);
            ViewModel.StatusMessage = "页面已保存。";
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed) ViewModel.StatusMessage = "页面未能保存，请等待任务结束后重试。"; }
        finally { _editingPages = false; }
    }

    private static IEnumerable<string> QuestionPageIds(Question question)
    {
        foreach (var source in question.Sources) yield return source.PageId;
        foreach (var image in question.Content.OfType<ImageContent>()) yield return image.Region.PageId;
        if (question.Attempt is { } attempt)
        {
            foreach (var source in attempt.StudentRegions) yield return source.PageId;
            if (attempt.Teacher is { } teacher) foreach (var source in teacher.Regions) yield return source.PageId;
        }
        foreach (var part in question.Parts) foreach (var id in QuestionPageIds(part)) yield return id;
    }

    private async void PrintOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_preparingPrint || _originalPrintSession is not null || _library is null || ViewModel.CurrentDocument is not { } document || ViewModel.IsSample) return;
        _preparingPrint = true;
        try
        {
            if (!PrintManager.IsSupported()) { ViewModel.StatusMessage = "此电脑暂时无法打印。"; return; }
            ViewModel.StatusMessage = "正在准备打印…";
            var pages = new List<BitmapImage>();
            var reader = new NativePageImages();
            foreach (var page in document.Pages)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (page.PreviewStatus != "ready") throw new InvalidDataException();
                var asset = await _library.ReadAssetAsync(page.AssetId, cancellationToken: _lifetime.Token);
                var rendered = await reader.ReadAsync(asset.Bytes, null, page.Rotation, _lifetime.Token);
                using var stream = new InMemoryRandomAccessStream();
                using (var writer = new DataWriter(stream))
                {
                    writer.WriteBytes(Convert.FromBase64String(rendered.Image.Base64));
                    await writer.StoreAsync().AsTask(_lifetime.Token); writer.DetachStream();
                }
                stream.Seek(0);
                var image = new BitmapImage(); await image.SetSourceAsync(stream).AsTask(_lifetime.Token); pages.Add(image);
            }
            if (pages.Count == 0) throw new InvalidDataException();
            _originalPrintSession = new OriginalPrintSession(this, document.Name, pages);
            bool shown = await PrintManagerInterop.ShowPrintUIForWindowAsync(WinRT.Interop.WindowNative.GetWindowHandle(this));
            if (!shown) { _originalPrintSession.Dispose(); _originalPrintSession = null; ViewModel.StatusMessage = "无法打开打印窗口，请检查打印机。"; }
            else ViewModel.StatusMessage = "请在打印窗口中选择打印机和纸张。";
        }
        catch (OperationCanceledException) { _originalPrintSession?.Dispose(); _originalPrintSession = null; }
        catch { _originalPrintSession?.Dispose(); _originalPrintSession = null; if (!_closed) ViewModel.StatusMessage = "打印未能准备完成，请检查原件和打印机后重试。"; }
        finally { _preparingPrint = false; }
    }

    private sealed class OriginalPrintSession : IDisposable
    {
        private readonly MainWindow _owner;
        private readonly string _title;
        private readonly PrintManager _manager;
        private readonly PrintDocument _document = new();
        private readonly IPrintDocumentSource _source;
        private readonly List<BitmapImage> _sources;
        private readonly List<Grid> _pages = [];
        private PrintTask? _task;
        private bool _disposed;
        public OriginalPrintSession(MainWindow owner, string title, List<BitmapImage> sources)
        {
            _owner = owner; _title = title; _sources = sources;
            _source = _document.DocumentSource;
            _manager = PrintManagerInterop.GetForWindow(WinRT.Interop.WindowNative.GetWindowHandle(owner));
            _document.Paginate += Paginate; _document.GetPreviewPage += Preview; _document.AddPages += AddPages;
            _manager.PrintTaskRequested += Requested;
            owner.Closed += WindowClosed;
        }
        private void Requested(PrintManager sender, PrintTaskRequestedEventArgs args)
        {
            _task = args.Request.CreatePrintTask(_title, source => source.SetSource(_source));
            _task.Completed += Completed;
        }
        private void Paginate(object sender, PaginateEventArgs args)
        {
            _pages.Clear();
            for (int index = 0; index < _sources.Count; index++)
            {
                var description = args.PrintTaskOptions.GetPageDescription((uint)index);
                var area = description.ImageableRect;
                var page = new Grid { Width = description.PageSize.Width, Height = description.PageSize.Height, Background = new SolidColorBrush(Microsoft.UI.Colors.White) };
                page.Children.Add(new UiImage { Source = _sources[index], Stretch = Stretch.Uniform, Width = area.Width, Height = area.Height, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(area.X, area.Y, 0, 0) });
                page.Measure(description.PageSize); page.Arrange(new Rect(new Point(), description.PageSize)); page.UpdateLayout();
                _pages.Add(page);
            }
            _document.SetPreviewPageCount(_pages.Count, PreviewPageCountType.Final);
        }
        private void Preview(object sender, GetPreviewPageEventArgs args)
        { if (args.PageNumber > 0 && args.PageNumber <= _pages.Count) _document.SetPreviewPage(args.PageNumber, _pages[args.PageNumber - 1]); }
        private void AddPages(object sender, AddPagesEventArgs args)
        { foreach (var page in _pages) _document.AddPage(page); _document.AddPagesComplete(); }
        private void Completed(PrintTask sender, PrintTaskCompletedEventArgs args) => _owner.DispatcherQueue.TryEnqueue(() =>
        {
            if (!_owner._closed) _owner.ViewModel.StatusMessage = args.Completion switch { PrintTaskCompletion.Submitted => "已交给打印机。", PrintTaskCompletion.Canceled => "已取消打印。", PrintTaskCompletion.Failed => "打印未完成，请检查打印机。", _ => "打印已结束。" };
            Dispose(); if (ReferenceEquals(_owner._originalPrintSession, this)) _owner._originalPrintSession = null;
        });
        private void WindowClosed(object sender, WindowEventArgs args) => Dispose();
        public void Dispose()
        {
            if (_disposed) return; _disposed = true;
            _manager.PrintTaskRequested -= Requested;
            _document.Paginate -= Paginate; _document.GetPreviewPage -= Preview; _document.AddPages -= AddPages;
            if (_task is not null) _task.Completed -= Completed;
            _owner.Closed -= WindowClosed;
            _pages.Clear(); _sources.Clear();
        }
    }
}
