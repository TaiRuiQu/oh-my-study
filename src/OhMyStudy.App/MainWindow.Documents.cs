using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using OhMyStudy.App.Services;
using OhMyStudy.App.Diagnostics;
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
        if (ViewModel.Questions.Any(item => item.IsDirty))
        { ViewModel.StatusMessage = UiText.Get("Save your changes before exporting."); return; }
        var saved = ViewModel.Questions.Where(item => item.Saved is not null).Select(item => item.Saved!).ToList();
        string title = ViewModel.CurrentDocument?.Name ?? UiText.Get("Questions");
        if (saved.Count == 0) { ViewModel.StatusMessage = UiText.Get("Organize and save questions first."); return; }
        var library = _library;
        if (library is null) { ViewModel.StatusMessage = UiText.Get("Open a library first."); return; }
        _exportingQuestions = true;
        try
        {
            var paper = new ComboBox { Header = UiText.Get("Paper size"), SelectedIndex = 0, Items = { UiText.Get("A4 portrait"), UiText.Get("A3 landscape") }, HorizontalAlignment = HorizontalAlignment.Stretch };
            var columns = new ComboBox { Header = UiText.Get("Columns"), SelectedIndex = 0, Items = { "1", "2", "3", "4" }, HorizontalAlignment = HorizontalAlignment.Stretch };
            var edition = new ComboBox { Header = UiText.Get("Content"), SelectedIndex = 0, Items = { UiText.Get("Questions"), UiText.Get("Answers and explanations") }, HorizontalAlignment = HorizontalAlignment.Stretch };
            var answerSpace = new CheckBox { Content = UiText.Get("Include answer space"), IsChecked = false };
            var distributeSpace = new CheckBox { Content = UiText.Get("Distribute remaining space evenly"), IsChecked = false, IsEnabled = false };
            void UpdateAnswerSpaceOptions()
            {
                answerSpace.IsEnabled = edition.SelectedIndex == 0;
                distributeSpace.IsEnabled = answerSpace.IsEnabled && answerSpace.IsChecked == true;
            }
            edition.SelectionChanged += (_, _) => UpdateAnswerSpaceOptions();
            answerSpace.Checked += (_, _) => UpdateAnswerSpaceOptions();
            answerSpace.Unchecked += (_, _) => UpdateAnswerSpaceOptions();
            bool columnsExplicit = false, settingDefault = false;
            columns.SelectionChanged += (_, _) => { if (!settingDefault) columnsExplicit = true; };
            paper.SelectionChanged += (_, _) =>
            {
                if (columnsExplicit) return;
                settingDefault = true;
                columns.SelectedIndex = paper.SelectedIndex == 1 ? 1 : 0;
                settingDefault = false;
            };
            var content = new StackPanel { Spacing = 12 };
            content.Children.Add(new TextBlock { Text = UiText.Format("Export currently displayed questions: {0}.", saved.Count), TextWrapping = TextWrapping.Wrap });
            content.Children.Add(paper); content.Children.Add(columns); content.Children.Add(edition);
            content.Children.Add(answerSpace);
            content.Children.Add(distributeSpace);
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = UiText.Get("Export questions"), Content = content, PrimaryButtonText = UiText.Get("Continue"), CloseButtonText = UiText.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            var size = paper.SelectedIndex == 1 ? ExamPaper.A3Landscape : ExamPaper.A4Portrait;
            var columnCount = columns.SelectedIndex switch { 1 => ExamColumns.Two, 2 => ExamColumns.Three, 3 => ExamColumns.Four, _ => ExamColumns.One };
            var version = edition.SelectedIndex == 1 ? ExamEdition.Answers : ExamEdition.Questions;
            bool includeAnswerSpace = version == ExamEdition.Questions && answerSpace.IsChecked == true;
            bool distributeAnswerSpace = includeAnswerSpace && distributeSpace.IsChecked == true;
            var printLabels = new ExamPrintLabels(UiText.Get("Class"), UiText.Get("Name"), UiText.Get("Student no."),
                UiText.Get("({0:g} points)"), UiText.Get("Answers and explanations"));
            if (version == ExamEdition.Questions && saved.Any(item => ContainsHandwrittenImage(item.Question)))
            { ViewModel.StatusMessage = UiText.Get("Some question images contain handwriting. Replace them with clean images before exporting a question paper."); return; }
            var picker = new FileSavePicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary, SuggestedFileName = title };
            picker.FileTypeChoices.Add("PDF", new List<string> { ".pdf" });
            WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
            var file = await picker.PickSaveFileAsync();
            if (file is null) return;
            ViewModel.StatusMessage = UiText.Get("Exporting…");
            int fallbackCount = 0;
            var bytes = await Task.Run(async () =>
            {
                var pages = (await library.ListDocumentsAsync(_lifetime.Token).ConfigureAwait(false))
                    .SelectMany(document => document.Pages).ToDictionary(page => page.Id, StringComparer.Ordinal);
                var reader = new NativePageImages();
                var imageBudget = new ExportImageBudget();
                var questions = new List<ExamQuestion>();
                for (int index = 0; index < saved.Count; index++)
                {
                    var question = saved[index].Question;
                    var body = await ExportSegmentsAsync(question.Stem, library, pages, reader, imageBudget, _lifetime.Token).ConfigureAwait(false);
                    if (question.Items is [var only])
                    {
                        body.AddRange(await ExportSegmentsAsync(only.Prompt, library, pages, reader, imageBudget, _lifetime.Token).ConfigureAwait(false));
                        questions.Add(new ExamQuestion(index + 1, body, only.Answer, only.Explanation, Options: only.Options,
                            Points: question.Points, QuestionType: question.QuestionType));
                        continue;
                    }
                    var parts = new List<ExamPart>();
                    foreach (var item in question.Items)
                        parts.Add(new ExamPart(item.Label, await ExportSegmentsAsync(item.Prompt, library, pages, reader, imageBudget, _lifetime.Token).ConfigureAwait(false), item.Answer,
                            Explanation: item.Explanation, Options: item.Options, Points: item.Points));
                    questions.Add(new ExamQuestion(index + 1, body, Parts: parts, Points: question.Points, QuestionType: question.QuestionType));
                }
                var document = new ExamDocument(title, questions, printLabels);
                var service = new TypstDocumentService(new(Path.Combine(AppContext.BaseDirectory, "native", "typst", "typst.exe")));
                var directory = Directory.CreateTempSubdirectory("OhMyStudy-Export-").FullName;
                try
                {
                    var result = await service.ExportExamAsync(document, size, version, directory, _lifetime.Token,
                        columns: columnCount, includeAnswerSpace: includeAnswerSpace, distributeAnswerSpace: distributeAnswerSpace).ConfigureAwait(false);
                    fallbackCount = result.Diagnostics.Count;
                    return await File.ReadAllBytesAsync(result.PdfPath, _lifetime.Token).ConfigureAwait(false);
                }
                finally { DeleteExportFiles(directory); }
            }, _lifetime.Token);
            _lifetime.Token.ThrowIfCancellationRequested();
            // The picker owns the destination; this is the first write of the complete verified PDF.
            await FileIO.WriteBytesAsync(file, bytes).AsTask(_lifetime.Token);
            ViewModel.StatusMessage = fallbackCount == 0 ? UiText.Get("PDF saved.") : UiText.Get("PDF saved. Some formulas were preserved as source text; please check them.");
        }
        catch (OperationCanceledException) { if (!_closed) ViewModel.StatusMessage = UiText.Get("Export canceled."); }
        catch (InvalidDataException error) when (error.Message.Contains("question image", StringComparison.OrdinalIgnoreCase)
            || error.Message == UiText.Get("The source of a question image no longer exists.") || error.Message == UiText.Get("Question images exceed the limit. Export in smaller batches."))
        { AppLog.Error("Export.Questions", error); if (!_closed) ViewModel.StatusMessage = UiText.Get(error.Message); }
        catch (ArgumentException error) { AppLog.Error("Export.Questions", error); if (!_closed) ViewModel.StatusMessage = UiText.Get("Question content or image regions exceed export limits. Check and export in smaller batches."); }
        catch (Exception error) { AppLog.Error("Export.Questions", error); if (!_closed) ViewModel.StatusMessage = UiText.Get("Export failed. Check the destination or reinstall the app and retry."); }
        finally { _exportingQuestions = false; }
    }

    private static bool ContainsHandwrittenImage(Question question) => question.Stem.Concat(question.Items.SelectMany(item => item.Prompt))
        .OfType<ImageContent>().Any(image => image.ContainsHandwriting);

    private static async Task<List<ExamSegment>> ExportSegmentsAsync(IEnumerable<Content> contents, OhMyStudy.Infrastructure.Storage.LibraryRepository library,
        IReadOnlyDictionary<string, SourcePage> pages, NativePageImages reader, ExportImageBudget imageBudget, CancellationToken cancellationToken)
    {
        var segments = new List<ExamSegment>();
        foreach (var content in contents)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (content)
            {
                case TextContent text: segments.Add(new(text.Text)); break;
                case FormulaContent formula: segments.Add(new(formula.Latex, true)); break;
                case TableContent table: segments.Add(new("", Rows: table.Rows.Select(row => (IReadOnlyList<string>)row.ToArray()).ToArray())); break;
                case ImageContent image:
                    if (!pages.TryGetValue(image.Region.PageId, out var page)) throw new InvalidDataException(UiText.Get("The source of a question image no longer exists."));
                    var asset = await library.ReadAssetAsync(image.AssetId ?? page.AssetId, cancellationToken: cancellationToken).ConfigureAwait(false);
                    var result = await reader.ReadForDisplayAsync(asset.Bytes, image.AssetId is null ? image.Region : null, image.AssetId is null ? page.Rotation : (ushort)0, cancellationToken).ConfigureAwait(false);
                    segments.Add(new("", Image: new ExamImage(imageBudget.Intern(Convert.FromBase64String(result.Image.Base64)), image.Caption, image.ContainsHandwriting, image.WidthPercent)));
                    break;
                case MermaidContent mermaid:
                    segments.Add(new("", Mermaid: new ExamMermaid(mermaid.Code, mermaid.Caption, mermaid.WidthPercent)));
                    break;
                default: throw new InvalidDataException(UiText.Get("Question content could not be read completely."));
            }
        }
        return segments;
    }

    private sealed class ExportImageBudget
    {
        private readonly Dictionary<string, byte[]> _images = new(StringComparer.Ordinal);
        private long _bytes;
        public byte[] Intern(byte[] bytes)
        {
            string hash = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(bytes));
            if (_images.TryGetValue(hash, out var existing)) return existing;
            if (bytes.Length > 64 * 1024 * 1024 || _images.Count >= 512 || _bytes + bytes.LongLength > 128L * 1024 * 1024)
                throw new InvalidDataException(UiText.Get("Question images exceed the limit. Export in smaller batches."));
            _images.Add(hash, bytes); _bytes += bytes.LongLength;
            return bytes;
        }
    }

    private static void DeleteExportFiles(string directory)
    {
        try
        {
            foreach (var name in new[] { "a4-questions", "a4-answers", "a3-questions", "a3-answers" })
                foreach (var extension in new[] { ".typ", ".pdf" }) File.Delete(Path.Combine(directory, name + extension));
            foreach (var pattern in new[] { "asset-*", "mermaid-*" })
                foreach (var asset in Directory.EnumerateFiles(directory, pattern)) File.Delete(asset);
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
            var notice = new TextBlock { Text = UiText.Get("Pages referenced by questions or reference answers cannot be removed."), TextWrapping = TextWrapping.Wrap };
            void Refresh()
            {
                rows.Children.Clear(); rows.Children.Add(notice);
                for (int index = 0; index < pages.Count; index++)
                {
                    int current = index;
                    var page = pages[index];
                    var line = new StackPanel { Spacing = 6 };
                    line.Children.Add(new TextBlock { Text = UiText.Format("Page {0} · {1} · {2}°", index + 1, (page.Role == "reference" ? UiText.Get("Reference answers") : page.Role == "student" ? UiText.Get("Student answer") : UiText.Get("Material")), page.Rotation), TextWrapping = TextWrapping.Wrap });
                    var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
                    var up = new Button { Content = UiText.Get("Move up"), IsEnabled = index > 0 };
                    up.Click += (_, _) => { (pages[current - 1], pages[current]) = (pages[current], pages[current - 1]); Refresh(); };
                    var down = new Button { Content = UiText.Get("Move down"), IsEnabled = index < pages.Count - 1 };
                    down.Click += (_, _) => { (pages[current + 1], pages[current]) = (pages[current], pages[current + 1]); Refresh(); };
                    var rotateLeft = new Button { Content = UiText.Get("Rotate left") };
                    rotateLeft.Click += (_, _) => { pages[current] = pages[current] with { Rotation = (ushort)((pages[current].Rotation + 270) % 360) }; Refresh(); };
                    var rotateRight = new Button { Content = UiText.Get("Rotate right") };
                    rotateRight.Click += (_, _) => { pages[current] = pages[current] with { Rotation = (ushort)((pages[current].Rotation + 90) % 360) }; Refresh(); };
                    var remove = new Button { Content = UiText.Get("Remove"), IsEnabled = pages.Count > 1 && !protectedPages.Contains(page.Id) };
                    if (protectedPages.Contains(page.Id)) ToolTipService.SetToolTip(remove, UiText.Get("This page is still referenced by questions or reference answers."));
                    remove.Click += (_, _) => { if (pages.Count > 1) { pages.RemoveAt(current); Refresh(); } };
                    actions.Children.Add(up); actions.Children.Add(down); actions.Children.Add(rotateLeft); actions.Children.Add(rotateRight); actions.Children.Add(remove);
                    line.Children.Add(actions);
                    rows.Children.Add(line);
                }
            }
            Refresh();
            var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = UiText.Get("Organize pages"), Content = new ScrollViewer { Content = rows, MaxHeight = 520, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto }, PrimaryButtonText = UiText.Get("Save"), CloseButtonText = UiText.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            await _library.UpdatePagesAsync(document.Id, pages.Select(page => new PageEdit { Id = page.Id, Rotation = page.Rotation }).ToList(), _lifetime.Token);
            foreach (var page in document.Pages) _pageCache.Invalidate(page.Id);
            await RefreshDocumentsAsync(document.Id);
            ViewModel.StatusMessage = UiText.Get("Pages saved.");
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed) ViewModel.StatusMessage = UiText.Get("Pages could not be saved. Wait for the task to finish and retry."); }
        finally { _editingPages = false; }
    }

    private static IEnumerable<string> QuestionPageIds(Question question)
    {
        foreach (var source in question.Sources) yield return source.PageId;
        foreach (var content in question.Stem.Concat(question.Items.SelectMany(item => item.Prompt)))
        {
            if (content is ImageContent image) yield return image.Region.PageId;
            else if (content is MermaidContent mermaid) yield return mermaid.Region.PageId;
        }
        foreach (var attempt in question.Items.Select(item => item.Attempt).OfType<Attempt>())
        {
            foreach (var source in attempt.StudentRegions) yield return source.PageId;
            if (attempt.Teacher is { } teacher) foreach (var source in teacher.Regions) yield return source.PageId;
        }
    }

    private async void PrintOriginal_Click(object sender, RoutedEventArgs e)
    {
        if (_preparingPrint || _originalPrintSession is not null || _library is null || ViewModel.CurrentDocument is not { } document || ViewModel.IsSample) return;
        _preparingPrint = true;
        try
        {
            if (!PrintManager.IsSupported()) { ViewModel.StatusMessage = UiText.Get("Printing is currently unavailable on this computer."); return; }
            ViewModel.StatusMessage = UiText.Get("Preparing to print…");
            var pages = new List<BitmapImage>();
            var reader = new NativePageImages();
            foreach (var page in document.Pages)
            {
                _lifetime.Token.ThrowIfCancellationRequested();
                if (page.PreviewStatus != "ready") throw new InvalidDataException();
                var asset = await _library.ReadAssetAsync(page.AssetId, cancellationToken: _lifetime.Token);
                var rendered = await reader.ReadForDisplayAsync(asset.Bytes, null, page.Rotation, _lifetime.Token);
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
            if (!shown) { _originalPrintSession.Dispose(); _originalPrintSession = null; ViewModel.StatusMessage = UiText.Get("Could not open the print dialog. Check your printer."); }
            else ViewModel.StatusMessage = "";
        }
        catch (OperationCanceledException) { _originalPrintSession?.Dispose(); _originalPrintSession = null; }
        catch { _originalPrintSession?.Dispose(); _originalPrintSession = null; if (!_closed) ViewModel.StatusMessage = UiText.Get("Could not prepare printing. Check the original and printer, then retry."); }
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
            if (!_owner._closed) _owner.ViewModel.StatusMessage = args.Completion switch { PrintTaskCompletion.Submitted => UiText.Get("Sent to printer."), PrintTaskCompletion.Canceled => UiText.Get("Printing canceled."), PrintTaskCompletion.Failed => UiText.Get("Printing failed. Check your printer."), _ => UiText.Get("Printing finished.") };
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
