using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Printing;
using Microsoft.UI.Windowing;
using Microsoft.UI.System;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.ViewModels;
using OhMyStudy.Documents;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics.Imaging;
using Windows.Graphics.Printing;
using Windows.Storage;
using Windows.Storage.Pickers;
using Windows.Storage.Streams;
using Windows.UI.ViewManagement;

namespace OhMyStudy.App;

public sealed partial class MainWindow : Window
{
    private const double PageWidth = 760;
    private const double PageHeight = 1040;
    private readonly string? _smokeReportPath;
    private readonly string? _smokePreviewPdfPath;
    private readonly string? _workflowReportPath;
    private bool _initialized;
    private bool _updatingZoom;
    private bool _hasImportedImage;
    private bool _openingFile;
    private int _rotation;
    private long _previewGeneration;
    private double _pageWidth = PageWidth;
    private double _pageHeight = PageHeight;
    private CancellationTokenSource? _previewCancellation;
    private string? _pdfPath;
    private int _pdfPageIndex;
    private int _pdfPageCount;
    private int _rasterWidth;
    private int _rasterHeight;
    private bool _closed;
    private readonly string? _performanceReportPath;
    private CancellationTokenSource? _questionPresentationCancellation;
    private readonly ThemeSettings _themeSettings;
    private sealed class PreviewInputException(string message) : Exception(message);

    public WorkspaceViewModel ViewModel { get; }

    public MainWindow(string? smokeReportPath = null, string? previewPdfPath = null, string? workflowReport = null, string? performanceReport = null)
    {
        _smokeReportPath = smokeReportPath;
        _smokePreviewPdfPath = previewPdfPath;
        _workflowReportPath = workflowReport;
        _performanceReportPath = performanceReport;
        ViewModel = new WorkspaceViewModel(sample: smokeReportPath is not null);
        InitializeComponent();
        InitializePaperMenu();
        QuestionContentHost.SizeChanged += QuestionContent_SizeChanged;
        InitializeSettingsLayout();
        InitializeWorkspaceAccessibility();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        AppWindow.Resize(new Windows.Graphics.SizeInt32(1440, 960));
        _themeSettings = ThemeSettings.CreateForWindowId(AppWindow.Id);
        _initialized = true;
        ViewModel.Questions.CollectionChanged += (_, _) => DispatcherQueue.TryEnqueue(() => { if (!_closed) UpdateQuestionState(); });
        ViewModel.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is nameof(ViewModel.IsTaskRunning) or nameof(ViewModel.CurrentDocument)) UpdateQuestionState();
        };
        UpdateQuestionState();
        ApplyWorkspaceLayout();
        _themeSettings.Changed += ThemeSettings_Changed;
        ApplyCaptionColors();
        AppWindow.Closing += AppWindow_Closing;
        Closed += (_, _) =>
        {
            _closed = true;
            _questionPresentationCancellation?.Cancel();
            _questionPresentationCancellation?.Dispose();
            _themeSettings.Changed -= ThemeSettings_Changed;
            CancelPreviewLoad();
            ReleaseReadingResources();
            _lifetime.Cancel();
            ViewModel.Dispose();
        };
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
        => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private void RootGrid_ActualThemeChanged(FrameworkElement sender, object args)
    {
        if (_initialized) DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            if (_closed) return;
            ApplyCaptionColors(); LocalizeNavigationSettings();
            ResetTaskTheme();
        });
    }

    private void ThemeSettings_Changed(ThemeSettings sender, object args)
        => DispatcherQueue.TryEnqueue(() => { if (!_closed) ApplyCaptionColors(); });

    private Windows.UI.Color CaptionForeground => _themeSettings.HighContrast
        ? new UISettings().GetColorValue(UIColorType.Foreground)
        : RootGrid.ActualTheme == ElementTheme.Dark ? Microsoft.UI.Colors.White : Microsoft.UI.Colors.Black;

    private void ApplyCaptionColors()
    {
        var bar = AppWindow.TitleBar;
        var foreground = CaptionForeground;
        bool highContrast = _themeSettings.HighContrast;
        var hoverForeground = highContrast ? new UISettings().GetColorValue(UIColorType.Background) : foreground;
        bar.ButtonForegroundColor = foreground;
        bar.ButtonHoverForegroundColor = hoverForeground;
        bar.ButtonPressedForegroundColor = hoverForeground;
        bar.ButtonInactiveForegroundColor = highContrast ? foreground : Windows.UI.Color.FromArgb(153, foreground.R, foreground.G, foreground.B);
        bar.ButtonHoverBackgroundColor = highContrast ? foreground : Windows.UI.Color.FromArgb(24, foreground.R, foreground.G, foreground.B);
        bar.ButtonPressedBackgroundColor = highContrast ? foreground : Windows.UI.Color.FromArgb(16, foreground.R, foreground.G, foreground.B);
    }

    private async void RootGrid_Loaded(object sender, RoutedEventArgs e)
    {
        // NavigationView creates its built-in SettingsItem when its template is applied.
        LocalizeNavigationSettings();
        DispatcherQueue.TryEnqueue(() => { if (!_closed) LocalizeNavigationSettings(); });
        FitPage();
        if (_smokeReportPath is not null)
        {
            ShowMaterialsNavigation();
            SourcePane.Visibility = Visibility.Visible;
            ApplyWorkspaceLayout();
            SamplePaper.Visibility = Visibility.Visible;
            WorkspaceNotice.IsOpen = true;
            DocumentToolbar.Visibility = Visibility.Collapsed;
        }
        else if (_performanceReportPath is not null)
        {
            await RunPerformanceSmokeAsync(_performanceReportPath);
            return;
        }
        else if (_workflowReportPath is not null)
        {
            await RunWorkflowSmokeAsync(_workflowReportPath);
            return;
        }
        else if (Array.IndexOf(Environment.GetCommandLineArgs(), "--preview-smoke") is int previewIndex && previewIndex > 0)
        {
            await RunPreviewSmokeAsync(Path.GetFullPath(Environment.GetCommandLineArgs()[previewIndex + 1]));
            return;
        }
        else if (Array.IndexOf(Environment.GetCommandLineArgs(), "--math-smoke") is int mathIndex && mathIndex > 0)
        {
            await RunMathSmokeAsync(Path.GetFullPath(Environment.GetCommandLineArgs()[mathIndex + 1]));
            return;
        }
        else if (Array.IndexOf(Environment.GetCommandLineArgs(), "--papers-smoke") is int papersIndex && papersIndex > 0)
        {
            await RunPapersSmokeAsync(Path.GetFullPath(Environment.GetCommandLineArgs()[papersIndex + 1])); return;
        }
        else if (Array.IndexOf(Environment.GetCommandLineArgs(), "--task-smoke") is int taskIndex && taskIndex > 0)
        {
            await RunTaskSmokeAsync(Path.GetFullPath(Environment.GetCommandLineArgs()[taskIndex + 1]));
            return;
        }
        else if (Array.IndexOf(Environment.GetCommandLineArgs(), "--import-smoke") is int importIndex && importIndex > 0)
        {
            await RunImportSmokeAsync(Path.GetFullPath(Environment.GetCommandLineArgs()[importIndex + 1]));
            return;
        }
        else
        {
            await InitializeLibraryAsync();
            await ShowQuestionLibraryAsync();
        }
        UpdateSourceHighlight();
        if (_smokeReportPath is not null)
        {
            // Exercise the actual loaded XAML tree, rather than declaring success from model construction.
            await Task.Delay(350);
            await RunSmokeTestAsync(_smokeReportPath);
        }
    }

    private async void Navigation_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
    {
        if (!_initialized || _suppressNavigation) return;
        RememberViewport();
        long generation = ++_documentGeneration;
        CancelPreviewLoad();
        // Use the native vector's identity lookup instead of unboxing an object-valued XAML Tag.
        int index = args.SelectedItem is null ? -1 : sender.MenuItems.IndexOf(args.SelectedItem);
        string tag = args.IsSettingsSelected ? "settings" : index switch { 1 => "library", 2 => "mistakes", 3 => "tasks", _ => "materials" };
        bool settings = tag == "settings";
        bool sampleMaterials = tag == "materials" && ViewModel.IsSample;
        SourcePane.Visibility = Visibility.Collapsed;
        if (SettingsPanel.Visibility == Visibility.Visible && !settings && HasUnsavedSettingsChanges)
        {
            var requested = args.SelectedItem;
            _suppressNavigation = true;
            try { Navigation.SelectedItem = Navigation.SettingsItem; }
            finally { _suppressNavigation = false; }
            if (!await ConfirmDiscardSettingsAsync()) return;
            DiscardSettingsForm();
            _suppressNavigation = true;
            try { Navigation.SelectedItem = requested; }
            finally { _suppressNavigation = false; }
        }
        SectionCaption.Visibility = tag == "materials" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPanel.Visibility = settings ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceActions.Visibility = tag == "materials" ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceStatus.Visibility = tag == "tasks" ? Visibility.Collapsed : Visibility.Visible;
        WorkspaceNotice.Visibility = tag == "materials" ? Visibility.Visible : Visibility.Collapsed;
        WorkspaceGrid.Visibility = settings || tag is "tasks" or "materials" && !sampleMaterials ? Visibility.Collapsed : Visibility.Visible;
        PapersPanel.Visibility = tag == "materials" && !sampleMaterials ? Visibility.Visible : Visibility.Collapsed;
        TasksPanel.Visibility = tag == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        DocumentToolbar.Visibility = Visibility.Collapsed;
        SectionCaption.Visibility = Visibility.Collapsed;
        GradeButton.Visibility = Visibility.Collapsed;
        ViewModel.ShowSection(tag);
        UpdateQuestionState();
        try
        {
            if (_library is not null)
            {
                if (tag is "library" or "mistakes" && !ViewModel.IsSample)
                {
                    var questions = await _library.ListQuestionsAsync(_lifetime.Token);
                    if (_closed || generation != _documentGeneration) return;
                    ViewModel.LoadLibrary(questions, tag == "mistakes");
                    await RestoreWorkspaceContextAsync(tag, generation);
                    EditQuestionButton.IsEnabled = ViewModel.SelectedQuestion.Saved is not null;
                }
                else if (tag == "materials") { await RefreshDocumentsAsync(); ShowPaperList(); }
                else if (tag == "tasks") await RefreshTasksAsync();
            }
        }
        catch (Exception error) { AppLog.Error("Navigation.Load", error); ViewModel.StatusMessage = "无法加载，请稍后重试。"; }
        if (sampleMaterials) ShowMaterialsNavigation();
        ApplyWorkspaceLayout();
    }

    private async void QuestionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        _questionPresentationTask = PresentSelectedQuestionAsync(clear: true);
        await _questionPresentationTask;
    }

    private Task _questionPresentationTask = Task.CompletedTask;
    private double _questionPresentationWidth;
    private int _questionLayoutRevision;

    private async void QuestionContent_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (!_initialized || _closed || Math.Abs(e.NewSize.Width - _questionPresentationWidth) < 2) return;
        int revision = ++_questionLayoutRevision;
        try
        {
            await Task.Delay(150, _lifetime.Token);
            if (revision == _questionLayoutRevision && !_closed)
            {
                _questionPresentationTask = PresentSelectedQuestionAsync(clear: false);
                await _questionPresentationTask;
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task PresentSelectedQuestionAsync(bool clear)
    {
        UpdateQuestionState();
        long generation = ++_questionPresentationGeneration;
        _questionPresentationCancellation?.Cancel();
        _questionPresentationCancellation?.Dispose();
        _questionPresentationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var cancellation = _questionPresentationCancellation.Token;
        _reviewSourceRegion = null;
        if (QuestionList.SelectedItem is QuestionViewModel selected) ViewModel.SelectedQuestion = selected;
        EditQuestionButton.IsEnabled = ViewModel.SelectedQuestion.Saved is not null;
        UpdateSourceHighlight();
        var question = ViewModel.SelectedQuestion.Saved;
        if (question is null || _library is null) { QuestionContentHost.Content = null; return; }
        _questionPresentationWidth = Math.Max(110, QuestionContentHost.ActualWidth);
        if (clear) QuestionContentHost.Content = new TextBlock { Text = "正在加载题目…" };
        try
        {
            var content = await OhMyStudy.App.Services.NativeQuestionPresenter.CreateAsync(question.Question, _library, cancellation, _questionPresentationWidth, RootGrid.ActualTheme);
            if (!_closed && generation == _questionPresentationGeneration && ReferenceEquals(ViewModel.SelectedQuestion.Saved, question))
            {
                QuestionContentHost.Content = content;
                RestoreQuestionContentOffset();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Question.Present", error); if (!_closed && generation == _questionPresentationGeneration) QuestionContentHost.Content = new TextBlock { Text = "题目内容暂时无法显示。" }; }
    }

    private void UpdateSourceHighlight()
    {
        if (SourceHighlight is null) return;
        if (_reviewSourceRegion is { } review && review.PageId == _sourcePageId)
        {
            SourceOverlay.Visibility = Visibility.Visible;
            Canvas.SetLeft(SourceHighlight, review.X * _pageWidth); Canvas.SetTop(SourceHighlight, review.Y * _pageHeight);
            SourceHighlight.Width = review.Width * _pageWidth; SourceHighlight.Height = review.Height * _pageHeight;
            return;
        }
        if (ViewModel.SelectedQuestion.Saved is { } saved)
        {
            var source = saved.Question.Sources.FirstOrDefault(r => r.PageId == _sourcePageId);
            SourceOverlay.Visibility = source is null ? Visibility.Collapsed : Visibility.Visible;
            LocateSourceButton.Visibility = saved.Question.Sources.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (source is not null)
            {
                Canvas.SetLeft(SourceHighlight, source.X * _pageWidth); Canvas.SetTop(SourceHighlight, source.Y * _pageHeight);
                SourceHighlight.Width = source.Width * _pageWidth; SourceHighlight.Height = source.Height * _pageHeight;
            }
            return;
        }
        Canvas.SetLeft(SourceHighlight, 48); Canvas.SetTop(SourceHighlight, ViewModel.SelectedQuestion.SourceY);
        SourceHighlight.Width = 664; SourceHighlight.Height = 188;
        // Synthetic coordinates belong only to the synthetic page and must never be attached to user images.
        SourceOverlay.Visibility = _hasImportedImage || !ViewModel.IsSample ? Visibility.Collapsed : Visibility.Visible;
        LocateSourceButton.Visibility = _hasImportedImage || !ViewModel.IsSample ? Visibility.Collapsed : Visibility.Visible;
    }

    private async void LocateSource_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.SelectedQuestion.Saved is { } saved && saved.Question.Sources.FirstOrDefault() is { } region && _library is not null)
        {
            try
            {
                if (ViewModel.CurrentDocument?.Id != saved.DocumentId) await LoadDocumentAsync(await _library.GetDocumentAsync(saved.DocumentId, _lifetime.Token));
                SourcePane.Visibility = Visibility.Visible; ApplyWorkspaceLayout();
                if (ViewModel.Questions.FirstOrDefault(q => q.Saved?.Id == saved.Id) is { } selected) ViewModel.SelectedQuestion = selected;
                var document = ViewModel.CurrentDocument!;
                int pageIndex = document.Pages.FindIndex(p => p.Id == region.PageId);
                if (pageIndex >= 0 && _sourcePageId != region.PageId) await ShowSourcePageAsync(pageIndex);
                var point = PageTransform.TransformPoint(new Windows.Foundation.Point((region.X + region.Width / 2) * _pageWidth, (region.Y + region.Height / 2) * _pageHeight));
                SourceViewer.ChangeView(Math.Max(0, point.X * SourceViewer.ZoomFactor - SourceViewer.ViewportWidth / 2), Math.Max(0, point.Y * SourceViewer.ZoomFactor - SourceViewer.ViewportHeight / 2), null, true);
            }
            catch { ViewModel.StatusMessage = "无法定位原件，请重新打开资料。"; }
            return;
        }
        if (_hasImportedImage)
        {
            ViewModel.StatusMessage = "当前题目没有对应的原件位置。";
            return;
        }
        // Source highlight rotates together with the page. Transform the center before scrolling.
        var source = new Windows.Foundation.Point(PageWidth / 2, ViewModel.SelectedQuestion.SourceY + 94);
        Windows.Foundation.Point center = PageTransform.TransformPoint(source);
        double zoom = SourceViewer.ZoomFactor;
        SourceViewer.ChangeView(Math.Max(0, center.X * zoom - SourceViewer.ViewportWidth / 2),
            Math.Max(0, center.Y * zoom - SourceViewer.ViewportHeight / 2), null, true);
        ViewModel.StatusMessage = "";
    }

    private async void RotateLeft_Click(object sender, RoutedEventArgs e) => await RotateSourceAsync(-90);
    private async void RotateRight_Click(object sender, RoutedEventArgs e) => await RotateSourceAsync(90);

    private void Rotate(int delta)
    {
        _rotation = (_rotation + delta + 360) % 360;
        bool sideways = _rotation is 90 or 270;
        PageCanvas.Width = sideways ? _pageHeight : _pageWidth;
        PageCanvas.Height = sideways ? _pageWidth : _pageHeight;
        PageTransform.Rotation = _rotation;
        PageTransform.TranslateX = (PageCanvas.Width - _pageWidth) / 2;
        PageTransform.TranslateY = (PageCanvas.Height - _pageHeight) / 2;
    }

    private void FitPage_Click(object sender, RoutedEventArgs e) => FitPage();

    private void FitPage()
    {
        if (SourceViewer.ViewportWidth <= 0 || SourceViewer.ViewportHeight <= 0) return;
        float factor = (float)Math.Clamp(Math.Min(
            (SourceViewer.ViewportWidth - 48) / PageCanvas.Width,
            (SourceViewer.ViewportHeight - 48) / PageCanvas.Height), 0.25, 4);
        SourceViewer.ChangeView(0, 0, factor, true);
        SetZoomControls(factor);
    }

    private void ZoomSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (!_initialized || _updatingZoom) return;
        SourceViewer.ChangeView(null, null, (float)(e.NewValue / 100), true);
        ZoomLabel.Text = $"{e.NewValue:0}%";
    }

    private void SourceViewer_ViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
    {
        if (_initialized) SetZoomControls(SourceViewer.ZoomFactor);
        if (_initialized && !e.IsIntermediate && !_settingRaster)
        {
            RememberPageViewport();
            ImproveReadingResolutionAsync();
        }
    }

    private void SetZoomControls(float factor)
    {
        _updatingZoom = true;
        ZoomSlider.Value = factor * 100;
        ZoomLabel.Text = $"{factor * 100:0}%";
        _updatingZoom = false;
    }

    private async void OpenFiles_Click(object sender, RoutedEventArgs e)
    {
        if (_openingFile) return;
        if (_library is null && !ViewModel.IsSample)
        {
            ViewModel.StatusMessage = "请先在设置中选择可用的资料库，再导入文件。";
            Navigation.SelectedItem = Navigation.SettingsItem; return;
        }
        _openingFile = true;
        try
        {
            if (_library is not null) await ImportFilesAsync(await PickFilesAsync());
            else
            {
                var files = await PickFilesAsync();
                if (files.Count > 0) await PreviewFileAsync(files[0]);
            }
        }
        catch (Exception exception)
        {
            SmokeStartupDiagnostics.Log("OpenFile", exception);
            ViewModel.StatusMessage = "无法打开文件，请重新选择。";
        }
        finally { _openingFile = false; }
    }

    private void Source_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = "导入图片或 PDF";
        }
    }

    private async void Source_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        if (_library is null && !ViewModel.IsSample)
        {
            ViewModel.StatusMessage = "请先在设置中选择可用的资料库，再导入文件。";
            Navigation.SelectedItem = Navigation.SettingsItem; return;
        }
        var deferral = e.GetDeferral();
        try
        {
            var items = await e.DataView.GetStorageItemsAsync();
            if (_library is not null)
            {
                var files = items.Where(i => i.IsOfType(StorageItemTypes.File)).Select(i => WinRT.CastExtensions.As<StorageFile>(i)).ToArray();
                await ImportFilesAsync(files); return;
            }
            if (items.Count != 1 || !items[0].IsOfType(StorageItemTypes.File))
            {
                ViewModel.StatusMessage = "请一次拖入一个文件。";
                return;
            }
            await PreviewFileAsync(WinRT.CastExtensions.As<StorageFile>(items[0]));
        }
        catch (Exception exception)
        {
            SmokeStartupDiagnostics.Log("DropFile", exception);
            ViewModel.StatusMessage = "无法打开文件，请重新选择。";
        }
        finally { deferral.Complete(); }
    }

    private async Task PreviewFileAsync(StorageFile file)
    {
        if (file.FileType.Equals(".pdf", StringComparison.OrdinalIgnoreCase))
        {
            await PreviewPdfAsync(file.Path, 0);
            return;
        }
        if (!new[] { ".png", ".jpg", ".jpeg", ".webp" }.Contains(file.FileType, StringComparer.OrdinalIgnoreCase))
        {
            ViewModel.StatusMessage = "请选择 PNG、JPEG、WebP 图片或 PDF。";
            return;
        }
        CancelPreviewLoad();
        long generation = _previewGeneration;
        var properties = await file.GetBasicPropertiesAsync();
        if (_closed || generation != _previewGeneration) return;
        if (properties.Size > 64UL * 1024 * 1024)
        {
            ViewModel.StatusMessage = "文件过大，请选择 64 MB 以内的图片。";
            return;
        }
        using var stream = await file.OpenAsync(FileAccessMode.Read);
        if (_closed || generation != _previewGeneration) return;
        // Inspect the dimensions before pixel decode; compressed byte size alone cannot bound image memory.
        var decoder = await BitmapDecoder.CreateAsync(stream);
        if (_closed || generation != _previewGeneration) return;
        uint width = decoder.PixelWidth;
        uint height = decoder.PixelHeight;
        if (width == 0 || height == 0 || width > 20_000 || height > 20_000 || (ulong)width * height > 40_000_000)
        {
            ViewModel.StatusMessage = "图片太大，请缩小或裁剪后重试。";
            return;
        }
        double scale = Math.Min(1, 1600d / Math.Max(width, height));
        int decodeWidth = Math.Max(1, (int)Math.Round(width * scale));
        int decodeHeight = Math.Max(1, (int)Math.Round(height * scale));
        stream.Seek(0);
        // Both decode dimensions are bounded, including very tall/narrow inputs. Full resolution tiling belongs to P6.
        var image = new BitmapImage
        {
            DecodePixelType = DecodePixelType.Physical,
            DecodePixelWidth = decodeWidth,
            DecodePixelHeight = decodeHeight
        };
        await image.SetSourceAsync(stream);
        if (_closed || generation != _previewGeneration) return;
        ImportedImage.Source = image;
        ImportedImage.Visibility = Visibility.Visible;
        SamplePaper.Visibility = Visibility.Collapsed;
        _hasImportedImage = true;
        _pdfPath = null;
        _pdfPageCount = 0;
        PdfPageControls.Visibility = Visibility.Collapsed;
        SetPageDimensions(image.PixelWidth, image.PixelHeight);
        OriginalCaption.Text = file.Name;
        WorkspaceNotice.Title = "示例题目";
        WorkspaceNotice.Message = "与当前原件无关，修改不会保存。";
        UpdateSourceHighlight();
        FitPage();
        ViewModel.StatusMessage = "";
    }

    private void CancelPreviewLoad()
    {
        _previewGeneration++;
        _previewCancellation?.Cancel();
        CancelPrefetch();
        CancelResolutionUpgrade();
    }

    private void CancelPreview_Click(object sender, RoutedEventArgs e)
    {
        CancelPreviewLoad();
        _previewCancellation = null;
        UpdatePdfControls(false);
        ViewModel.StatusMessage = "已取消加载。";
    }

    private async void PreviousPdfPage_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsSample && ViewModel.CurrentDocument is not null) { await ShowSourcePageAsync(_sourcePageIndex - 1); return; }
        if (_pdfPath is not null && _pdfPageIndex > 0) await PreviewPdfAsync(_pdfPath, _pdfPageIndex - 1);
    }

    private async void NextPdfPage_Click(object sender, RoutedEventArgs e)
    {
        if (!ViewModel.IsSample && ViewModel.CurrentDocument is not null) { await ShowSourcePageAsync(_sourcePageIndex + 1); return; }
        if (_pdfPath is not null && _pdfPageIndex + 1 < _pdfPageCount) await PreviewPdfAsync(_pdfPath, _pdfPageIndex + 1);
    }

    private void UpdatePdfControls(bool loading)
    {
        PdfPageControls.Visibility = loading || _pdfPath is not null ? Visibility.Visible : Visibility.Collapsed;
        PdfPageLabel.Text = loading ? "正在加载页面…" : $"第 {_pdfPageIndex + 1} / {_pdfPageCount} 页";
        PreviousPdfPage.IsEnabled = !loading && _pdfPageIndex > 0;
        NextPdfPage.IsEnabled = !loading && _pdfPageIndex + 1 < _pdfPageCount;
        CancelPreview.IsEnabled = loading;
    }

    private async Task<bool> PreviewPdfAsync(string path, int pageIndex)
    {
        CancelPreviewLoad();
        long generation = _previewGeneration;
        using var cancellation = new CancellationTokenSource();
        _previewCancellation = cancellation;
        UpdatePdfControls(true);
        ViewModel.StatusMessage = "正在打开…";
        try
        {
            var rendered = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var file = new FileInfo(Path.GetFullPath(path));
                if (!file.Exists || file.Length is <= 0 or > 64L * 1024 * 1024)
                    throw new PreviewInputException("请选择非空且不超过 64 MB 的 PDF 文件。");
                var rasterizer = new PdfiumRasterizer(Path.Combine(AppContext.BaseDirectory, "native", "pdfium.dll"));
                using var document = rasterizer.Open(file.FullName);
                int count = document.PageCount;
                if (count is < 1 or > 12) throw new PreviewInputException("请选择 1 至 12 页的 PDF 文件。");
                var page = document.RenderPage(pageIndex, 1600, 0, cancellation.Token);
                return (PageCount: count, Page: page);
            }, cancellation.Token);
            if (_closed || generation != _previewGeneration) return false;

            BitmapImage image = await CreateRasterImageAsync(rendered.Page);
            cancellation.Token.ThrowIfCancellationRequested();
            if (_closed || generation != _previewGeneration) return false;
            ImportedImage.Source = image;
            ImportedImage.Visibility = Visibility.Visible;
            SamplePaper.Visibility = Visibility.Collapsed;
            _hasImportedImage = true;
            _pdfPath = Path.GetFullPath(path);
            _pdfPageIndex = pageIndex;
            _pdfPageCount = rendered.PageCount;
            _rasterWidth = rendered.Page.Width;
            _rasterHeight = rendered.Page.Height;
            SetPageDimensions(rendered.Page.Width, rendered.Page.Height);
            OriginalCaption.Text = Path.GetFileName(path);
            WorkspaceNotice.Title = "示例题目";
            WorkspaceNotice.Message = "与当前原件无关，修改不会保存。";
            UpdateSourceHighlight();
            FitPage();
            ViewModel.StatusMessage = "";
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed && generation == _previewGeneration) ViewModel.StatusMessage = "已取消加载。";
            return false;
        }
        catch (PreviewInputException exception)
        {
            if (!_closed && generation == _previewGeneration) ViewModel.StatusMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            SmokeStartupDiagnostics.Log("PreviewPdf", exception);
            if (!_closed && generation == _previewGeneration) ViewModel.StatusMessage = "无法打开这份 PDF，请检查文件是否完整或换一份重试。";
            return false;
        }
        finally
        {
            if (ReferenceEquals(_previewCancellation, cancellation))
            {
                _previewCancellation = null;
                if (!_closed) UpdatePdfControls(false);
            }
        }
    }

    private static async Task<BitmapImage> CreateRasterImageAsync(RasterizedPage page)
    {
        if (page.Stride != checked(page.Width * 4) || page.Bgra32.Length != checked(page.Stride * page.Height))
            throw new InvalidDataException("PDF 栅格行布局无效。 ");
        // Encode only to an in-memory stream. User pages are never written to a temporary disk file.
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.BmpEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)page.Width, (uint)page.Height, 96, 96, page.Bgra32);
        await encoder.FlushAsync();
        stream.Seek(0);
        var image = new BitmapImage();
        await image.SetSourceAsync(stream);
        return image;
    }

    private void SetPageDimensions(double width, double height)
    {
        if (width <= 0 || height <= 0) throw new InvalidDataException("页面尺寸无效。 ");
        _pageWidth = PageWidth;
        _pageHeight = PageWidth * height / width;
        PaperPage.Width = _pageWidth;
        PaperPage.Height = _pageHeight;
        PageTransform.CenterX = _pageWidth / 2;
        PageTransform.CenterY = _pageHeight / 2;
        Rotate(0);
    }

    private void RestoreSample_Click(object sender, RoutedEventArgs e)
    {
        RememberViewport(); ViewModel.LoadSample(); ShowMaterialsNavigation();
        SourcePane.Visibility = Visibility.Visible; DocumentCommands.Visibility = Visibility.Collapsed; SetPaperMenuDetail(false);
        WorkspaceNotice.Title = "示例资料"; WorkspaceNotice.Message = "可查看和编辑，修改不会保存。"; WorkspaceNotice.IsOpen = true;
        _rotation = 0;
        GradeButton.IsEnabled = false; DocumentToolbar.Visibility = Visibility.Visible;
        CancelPreviewLoad();
        ImportedImage.Source = null;
        ImportedImage.Visibility = Visibility.Collapsed;
        SamplePaper.Visibility = Visibility.Visible;
        _hasImportedImage = false;
        _pdfPath = null;
        _pdfPageCount = 0;
        PdfPageControls.Visibility = Visibility.Collapsed;
        SetPageDimensions(PageWidth, PageHeight);
        OriginalCaption.Text = "";
        WorkspaceNotice.Title = "示例资料";
        WorkspaceNotice.Message = "可查看和编辑，修改不会保存。";
        UpdateSourceHighlight();
        ViewModel.StatusMessage = ""; ApplyWorkspaceLayout();
    }

    private void ThemePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized) return;
        RootGrid.RequestedTheme = ThemePicker.SelectedIndex switch
        {
            1 => ElementTheme.Light,
            2 => ElementTheme.Dark,
            _ => ElementTheme.Default
        };
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = "Oh My Study",
            RequestedTheme = RootGrid.ActualTheme,
            Content = "整理资料，梳理错题，轻松复习。",
            CloseButtonText = "知道了",
            DefaultButton = ContentDialogButton.Close
        };
        await dialog.ShowAsync();
    }

    private async Task RunSmokeTestAsync(string reportPath)
    {
        var report = new DesktopSmokeReport
        {
            TimestampUtc = DateTimeOffset.UtcNow,
            IsNativeAot = !RuntimeFeature.IsDynamicCodeSupported,
            ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
            SyntheticDataOnly = _smokePreviewPdfPath is null,
            PdfRequested = _smokePreviewPdfPath is not null
        };
        int exitCode = 1;
        try
        {
            report.WindowLoaded = RootGrid.XamlRoot is not null && RootGrid.ActualWidth > 0 && AppWindow.IsVisible;
            report.TitleBarHeight = AppTitleBar.ActualHeight;
            double navigationTop = Navigation.TransformToVisual(RootGrid).TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
            report.UnifiedTitleBar = ExtendsContentIntoTitleBar && AppWindow.TitleBar.ExtendsContentIntoTitleBar &&
                AppWindow.TitleBar.PreferredHeightOption == TitleBarHeightOption.Tall &&
                Math.Abs(report.TitleBarHeight - 48) < 1 && Math.Abs(navigationTop - report.TitleBarHeight) < 1 &&
                AppTitleBar.Title == Title && AppTitleBar.IconSource is not null && string.IsNullOrEmpty(Navigation.PaneTitle);
            bool paneWasOpen = Navigation.IsPaneOpen;
            AppTitleBar_PaneToggleRequested(AppTitleBar, EventArgs.Empty);
            await Task.Delay(250);
            bool paneChanged = Navigation.IsPaneOpen != paneWasOpen;
            AppTitleBar_PaneToggleRequested(AppTitleBar, EventArgs.Empty);
            await Task.Delay(250);
            report.TitleBarPaneToggle = paneChanged && Navigation.IsPaneOpen == paneWasOpen &&
                AppTitleBar.IsPaneToggleButtonVisible && !Navigation.IsPaneToggleButtonVisible;
            Navigation.SelectedItem = Navigation.MenuItems[1];
            await Task.Delay(30);
            bool libraryNavigation = ViewModel.SectionTitle == "题库" && ViewModel.Questions.Count == 500;
            string libraryDetail = $"library: {ViewModel.SectionTitle}, {ViewModel.Questions.Count}, index={Navigation.MenuItems.IndexOf(Navigation.SelectedItem)}";
            Navigation.SelectedItem = Navigation.MenuItems[2];
            await Task.Delay(30);
            bool mistakeNavigation = ViewModel.SectionTitle == "错题" && ViewModel.Questions.Count is > 0 and < 500;
            string mistakeDetail = $"mistakes: {ViewModel.SectionTitle}, {ViewModel.Questions.Count}, index={Navigation.MenuItems.IndexOf(Navigation.SelectedItem)}";
            Navigation.SelectedItem = Navigation.SettingsItem;
            await Task.Delay(30);
            bool settingsNavigation = SettingsPanel.Visibility == Visibility.Visible &&
                Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(
                    WinRT.CastExtensions.As<NavigationViewItem>(Navigation.SettingsItem)) == "设置";
            string settingsDetail = $"settings: {SettingsPanel.Visibility}, {Microsoft.UI.Xaml.Automation.AutomationProperties.GetName(WinRT.CastExtensions.As<NavigationViewItem>(Navigation.SettingsItem))}";
            Navigation.SelectedItem = Navigation.MenuItems[0];
            await Task.Delay(30);
            report.NativeNavigation = libraryNavigation && mistakeNavigation && settingsNavigation &&
                WorkspaceGrid.Visibility == Visibility.Visible && ViewModel.SectionTitle == "试卷";
            report.NavigationDetails = [libraryDetail, mistakeDetail, settingsDetail, $"returned: {ViewModel.SectionTitle}, {WorkspaceGrid.Visibility}"];
            var selected = ViewModel.SelectedQuestion;
            string originalAnswer = selected.Answer;
            selected.Answer = "原生绑定自检 · 中文输入";
            await Task.Delay(30);
            report.CompiledBinding = AnswerEditor.Text == selected.Answer;
            AnswerEditor.Text = "双向绑定自检";
            report.TwoWayBinding = selected.Answer == "双向绑定自检";
            selected.Answer = originalAnswer;

            QuestionList.UpdateLayout();
            report.TotalItems = QuestionList.Items.Count;
            report.RealizedItems = Enumerable.Range(0, report.TotalItems).Count(index => QuestionList.ContainerFromIndex(index) is not null);
            // Native AOT can expose the native panel through its base Panel projection. Ask IInspectable for
            // the actual native class instead of treating a failed managed 'is' as evidence about virtualization.
            DependencyObject? panel = QuestionList.ItemsPanelRoot;
            if (panel is null || GetRuntimeClassName(panel) != "Microsoft.UI.Xaml.Controls.ItemsStackPanel")
                panel = FindNativeItemsStackPanel(QuestionList);
            report.PanelType = panel?.GetType().FullName;
            report.RuntimeClass = panel is null ? null : GetRuntimeClassName(panel);
            report.ItemsStackPanel = report.RuntimeClass == "Microsoft.UI.Xaml.Controls.ItemsStackPanel";
            if (panel is not null)
            {
                var projectedPanel = WinRT.CastExtensions.As<ItemsStackPanel>(panel);
                report.PanelOrientationType = projectedPanel.GetType().FullName;
                report.PanelOrientationValue = projectedPanel.Orientation.ToString();
                report.PanelOrientationVertical = projectedPanel.Orientation == Orientation.Vertical;
            }

            QuestionList.ScrollIntoView(ViewModel.Questions[^1], ScrollIntoViewAlignment.Leading);
            report.LastItemRealized = await WaitForContainerAsync(report.TotalItems - 1);
            report.RealizedItemsAtEnd = Enumerable.Range(0, report.TotalItems).Count(index => QuestionList.ContainerFromIndex(index) is not null);
            QuestionList.ScrollIntoView(ViewModel.Questions[0], ScrollIntoViewAlignment.Leading);
            report.ScrollRoundTrip = report.LastItemRealized && await WaitForContainerAsync(0);
            report.Virtualization = report.TotalItems == 500 && report.RealizedItems is > 0 and < 500 &&
                report.RealizedItemsAtEnd is > 0 and < 500 && report.ItemsStackPanel && report.PanelOrientationVertical && report.ScrollRoundTrip;

            ProbePrintInterop(report);

            RootGrid.RequestedTheme = ElementTheme.Light;
            await Task.Delay(100);
            bool light = RootGrid.ActualTheme == ElementTheme.Light;
            bool lightCaption = AppWindow.TitleBar.ButtonForegroundColor == CaptionForeground;
            RootGrid.RequestedTheme = ElementTheme.Dark;
            await Task.Delay(100);
            report.ThemeSwitch = light && RootGrid.ActualTheme == ElementTheme.Dark;
            report.TitleBarCaptionTheme = lightCaption && AppWindow.TitleBar.ButtonForegroundColor == CaptionForeground;
            RootGrid.RequestedTheme = ElementTheme.Default;
            var resources = Microsoft.UI.Xaml.Application.Current.Resources.MergedDictionaries[1];
            report.HighContrastResources = resources.ThemeDictionaries.ContainsKey("HighContrast");

            Rotate(90);
            report.RotationAndSource = PageCanvas.Width == PageHeight && PageCanvas.Height == PageWidth &&
                PageTransform.Rotation == 90 && Canvas.GetTop(SourceHighlight) == selected.SourceY;
            Rotate(-90);

            Task task = ViewModel.RunSyntheticTaskAsync();
            await Task.Delay(60);
            ViewModel.CancelTaskCommand.Execute(null);
            await task;
            report.Cancellation = !ViewModel.IsTaskRunning && ViewModel.TaskStatus.Contains("已取消", StringComparison.Ordinal);
            if (_smokePreviewPdfPath is not null)
            {
                report.PdfLoaded = await PreviewPdfAsync(_smokePreviewPdfPath, 0);
                report.PageCount = _pdfPageCount;
                if (report.PdfLoaded && report.PageCount > 1)
                {
                    bool next = await PreviewPdfAsync(_smokePreviewPdfPath, 1) && _pdfPageIndex == 1;
                    bool previous = await PreviewPdfAsync(_smokePreviewPdfPath, 0) && _pdfPageIndex == 0;
                    report.PdfPageNavigation = next && previous;
                }
                else report.PdfPageNavigation = report.PdfLoaded && !PreviousPdfPage.IsEnabled && !NextPdfPage.IsEnabled;
                report.RasterDimensions = $"{_rasterWidth}x{_rasterHeight}";
                report.PdfPreview = report.PdfLoaded && report.PageCount is >= 1 and <= 12 &&
                    _rasterWidth > 0 && _rasterHeight > 0 && Math.Max(_rasterWidth, _rasterHeight) == 1600 &&
                    ImportedImage.Source is BitmapImage && SourceOverlay.Visibility == Visibility.Collapsed && report.PdfPageNavigation;
                if (!report.PdfLoaded) report.Error = ViewModel.StatusMessage;
            }
            else report.PdfPreview = true;
            report.ScreenshotPath = Path.ChangeExtension(reportPath, ".png");
            await Task.Delay(60);
            await CaptureWorkspaceAsync(report.ScreenshotPath);
            report.ScreenshotCaptured = File.Exists(report.ScreenshotPath);
            report.Passed = report.WindowLoaded && report.UnifiedTitleBar && report.TitleBarPaneToggle && report.NativeNavigation && report.CompiledBinding && report.TwoWayBinding && report.Virtualization &&
                report.ThemeSwitch && report.TitleBarCaptionTheme && report.HighContrastResources && report.RotationAndSource && report.Cancellation && report.ScreenshotCaptured && report.PdfPreview && report.PrintSupported && report.PrintInterop;
            exitCode = report.Passed ? 0 : 1;
        }
        catch (Exception exception) { report.Error = exception.ToString(); }
        finally
        {
            try
            {
                string? directory = Path.GetDirectoryName(reportPath);
                if (directory is not null) Directory.CreateDirectory(directory);
                await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, SmokeJsonContext.Default.DesktopSmokeReport));
            }
            catch { exitCode = 3; }
            Close();
            Environment.Exit(exitCode);
        }
    }

    private async Task<bool> WaitForContainerAsync(int index)
    {
        for (int attempt = 0; attempt < 12; attempt++)
        {
            await Task.Delay(30);
            QuestionList.UpdateLayout();
            if (QuestionList.ContainerFromIndex(index) is not null) return true;
        }
        return false;
    }

    private static string? GetRuntimeClassName(object value)
    {
        if (!WinRT.ComWrappersSupport.TryUnwrapObject(value, out var reference)) return null;
        return new WinRT.IInspectable(reference).GetRuntimeClassName(noThrow: true);
    }

    private static DependencyObject? FindNativeItemsStackPanel(DependencyObject root)
    {
        if (GetRuntimeClassName(root) == "Microsoft.UI.Xaml.Controls.ItemsStackPanel") return root;
        for (int index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            if (FindNativeItemsStackPanel(VisualTreeHelper.GetChild(root, index)) is { } panel) return panel;
        return null;
    }

    private void ProbePrintInterop(DesktopSmokeReport report)
    {
        try
        {
            report.PrintSupported = PrintManager.IsSupported();
            var manager = PrintManagerInterop.GetForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
            var printDocument = new PrintDocument();
            Windows.Foundation.TypedEventHandler<PrintManager, PrintTaskRequestedEventArgs> requested = (_, _) => { };
            PaginateEventHandler paginate = (_, _) => { };
            GetPreviewPageEventHandler preview = (_, _) => { };
            AddPagesEventHandler addPages = (_, _) => { };
            // Register and remove the real native event projections, without opening UI or creating a print task.
            manager.PrintTaskRequested += requested;
            try
            {
                printDocument.Paginate += paginate;
                printDocument.GetPreviewPage += preview;
                printDocument.AddPages += addPages;
                try { report.PrintInterop = printDocument.DocumentSource is not null; }
                finally
                {
                    printDocument.AddPages -= addPages;
                    printDocument.GetPreviewPage -= preview;
                    printDocument.Paginate -= paginate;
                }
            }
            finally { manager.PrintTaskRequested -= requested; }
            if (!report.PrintSupported) report.PrintError = "Windows 当前报告不支持打印；仅验证了原生 API，不代表系统或实物打印通过。";
        }
        catch (Exception exception)
        {
            report.PrintInterop = false;
            report.PrintError = exception.ToString();
        }
    }

    private async Task CaptureWorkspaceAsync(string path, FrameworkElement? element = null)
    {
        string directoryPath = Path.GetDirectoryName(path) ?? throw new InvalidOperationException("截图需要绝对路径。");
        Directory.CreateDirectory(directoryPath);
        byte[] pixels;
        int pixelWidth, pixelHeight;
        if (element is null)
        {
            // Every full-window smoke screenshot reads the window's own composed surface.
            // PrintWindow never raises, activates or makes the window topmost, so the app
            // may stay behind other windows while the capture runs. Element-only captures
            // still use RenderTargetBitmap because they deliberately crop one visual.
            await Task.Delay(180);
            (pixels, pixelWidth, pixelHeight) = CaptureNativeClient();
        }
        else
        {
            var bitmap = new RenderTargetBitmap();
            await bitmap.RenderAsync(element ?? RootGrid);
            var buffer = await bitmap.GetPixelsAsync();
            using var reader = DataReader.FromBuffer(buffer);
            pixels = new byte[buffer.Length];
            reader.ReadBytes(pixels);
            pixelWidth = bitmap.PixelWidth; pixelHeight = bitmap.PixelHeight;
        }
        StorageFolder directory = await StorageFolder.GetFolderFromPathAsync(directoryPath);
        StorageFile file = await directory.CreateFileAsync(Path.GetFileName(path), CreationCollisionOption.ReplaceExisting);
        using var stream = await file.OpenAsync(FileAccessMode.ReadWrite);
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
            (uint)pixelWidth, (uint)pixelHeight, 96, 96, pixels);
        await encoder.FlushAsync();
    }

}
