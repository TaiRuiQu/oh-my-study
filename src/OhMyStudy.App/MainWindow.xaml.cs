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
using OhMyStudy.Domain;
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
    private CancellationTokenSource? _questionPresentationCancellation;
    private readonly ThemeSettings _themeSettings;
    private Services.WindowBounds _windowBounds = Services.WindowBounds.Default;
    private sealed class PreviewInputException(string message) : Exception(message);

    public WorkspaceViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new WorkspaceViewModel();
        InitializeComponent();
        QuestionColumnSplitter.ResizeRequested += (_, change) => ResizeQuestionSplit(change);
        InitializePaneSplitters();
        InitializeLanguagePicker();
        InitializePaperMenu();
        BankList.ItemsSource = _bankQuestions;
        QuestionContentHost.SizeChanged += QuestionContent_SizeChanged;
        InitializeSettingsLayout();
        InitializeWorkspaceAccessibility();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        AppWindow.TitleBar.ButtonBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.TitleBar.ButtonInactiveBackgroundColor = Microsoft.UI.Colors.Transparent;
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "App.ico"));
        RestoreWindowBounds();
        AppWindow.Changed += (_, _) => CaptureWindowBounds();
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
            CancelOpenPresentation();
            _themeSettings.Changed -= ThemeSettings_Changed;
            CancelPreviewLoad();
            ReleaseReadingResources();
            _lifetime.Cancel();
            ViewModel.Dispose();
            _restartAfterClose?.Invoke();
        };
    }

    private void AppTitleBar_PaneToggleRequested(TitleBar sender, object args)
        => Navigation.IsPaneOpen = !Navigation.IsPaneOpen;

    private static string WindowStatePath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMyStudy", "window.txt");

    private static bool DiagnosticLaunch => Services.WindowBounds.IsDiagnosticLaunch(Environment.GetCommandLineArgs());

    private OverlappedPresenter? WindowPresenter => AppWindow.Presenter is { Kind: AppWindowPresenterKind.Overlapped } presenter
        ? WinRT.CastExtensions.As<OverlappedPresenter>(presenter) : null;

    private void RestoreWindowBounds(string? statePath = null)
    {
        _windowBounds = Services.WindowBounds.Default;
        if (statePath is not null || !DiagnosticLaunch)
        {
            try
            {
                string path = statePath ?? WindowStatePath;
                if (File.Exists(path)) _windowBounds = Services.WindowBounds.Parse(File.ReadAllText(path));
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppLog.Error("Window.RestoreBounds", error); }
        }
        bool maximized = _windowBounds.Maximized;
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        AppWindow.Resize(new(Math.Min(_windowBounds.Width, area.Width), Math.Min(_windowBounds.Height, area.Height)));
        if (maximized && WindowPresenter is { } overlapped) overlapped.Maximize();
    }

    private void CaptureWindowBounds()
    {
        if (WindowPresenter is not { } presenter) return;
        if (presenter.State == OverlappedPresenterState.Maximized) _windowBounds = _windowBounds with { Maximized = true };
        else if (presenter.State == OverlappedPresenterState.Restored && AppWindow.Size is { Width: >= 460, Height: >= 420 } size)
            _windowBounds = new(size.Width, size.Height, false);
    }

    private void RememberWindowBounds(string? statePath = null)
    {
        if (statePath is null && DiagnosticLaunch) return;
        try
        {
            CaptureWindowBounds();
            string path = statePath ?? WindowStatePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, _windowBounds.ToString());
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { AppLog.Error("Window.SaveBounds", error); }
    }

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
        await InitializeLibraryAsync();
        await ShowQuestionLibraryAsync();
        UpdateSourceHighlight();
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
        bool questions = tag is "library" or "mistakes";
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
        WorkspaceGrid.Visibility = settings || questions || tag is "tasks" or "materials" && !sampleMaterials ? Visibility.Collapsed : Visibility.Visible;
        if (!questions) HideQuestionPanels();
        PapersPanel.Visibility = tag == "materials" && !sampleMaterials ? Visibility.Visible : Visibility.Collapsed;
        TasksPanel.Visibility = tag == "tasks" ? Visibility.Visible : Visibility.Collapsed;
        DocumentToolbar.Visibility = Visibility.Collapsed;
        SectionCaption.Visibility = Visibility.Collapsed;
        GradeButton.Visibility = Visibility.Collapsed;
        ViewModel.ShowSection(tag);
        UpdateQuestionState();
        try
        {
            if (questions) await ShowQuestionBankAsync(tag);
            else if (_library is not null)
            {
                if (tag == "materials") { await RefreshDocumentsAsync(); ShowPaperList(); }
                else if (tag == "tasks") await RefreshTasksAsync();
            }
        }
        catch (Exception error) { AppLog.Error("Navigation.Load", error); ViewModel.StatusMessage = UiText.Get("Could not load. Try again later."); }
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
        StudentAnswerButton.IsEnabled = ViewModel.SelectedQuestion.Saved is not null;
        UpdateSourceHighlight();
        var question = ViewModel.SelectedQuestion.Saved;
        if (question is null || _library is null) { QuestionContentHost.Content = null; return; }
        _questionPresentationWidth = Math.Max(110, QuestionContentHost.ActualWidth);
        if (clear) QuestionContentHost.Content = new TextBlock { Text = UiText.Get("Loading question…") };
        try
        {
            var selectedQuestion = ViewModel.SelectedQuestion;
            var content = await OhMyStudy.App.Services.NativeQuestionPresenter.CreateAsync(question.Question, _library, cancellation, _questionPresentationWidth, RootGrid.ActualTheme,
                confirmResult: () => ConfirmQuestionAsync(selectedQuestion));
            if (!_closed && generation == _questionPresentationGeneration && ReferenceEquals(ViewModel.SelectedQuestion.Saved, question))
            {
                QuestionContentHost.Content = content;
                RestoreQuestionContentOffset();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Question.Present", error); if (!_closed && generation == _questionPresentationGeneration) QuestionContentHost.Content = new TextBlock { Text = UiText.Get("Question content is temporarily unavailable.") }; }
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
        if (ViewModel.SelectedQuestion.Saved is { } saved) { await LocateQuestionAsync(saved); return; }
        if (_hasImportedImage)
        {
            ViewModel.StatusMessage = UiText.Get("This question has no corresponding source location.");
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

    private async Task LocateQuestionAsync(SavedQuestion saved)
    {
        if (saved.Question.Sources.FirstOrDefault() is { } region && _library is not null)
        {
            try
            {
                if (ViewModel.CurrentDocument?.Id != saved.DocumentId || WorkspaceGrid.Visibility != Visibility.Visible) await LoadDocumentAsync(await _library.GetDocumentAsync(saved.DocumentId, _lifetime.Token));
                SourcePane.Visibility = Visibility.Visible; ApplyWorkspaceLayout();
                if (ViewModel.Questions.FirstOrDefault(q => q.Saved?.Id == saved.Id) is { } selected) ViewModel.SelectedQuestion = selected;
                var document = ViewModel.CurrentDocument!;
                int pageIndex = document.Pages.FindIndex(p => p.Id == region.PageId);
                if (pageIndex >= 0 && _sourcePageId != region.PageId) await ShowSourcePageAsync(pageIndex);
                var point = PageTransform.TransformPoint(new Windows.Foundation.Point((region.X + region.Width / 2) * _pageWidth, (region.Y + region.Height / 2) * _pageHeight));
                SourceViewer.ChangeView(Math.Max(0, point.X * SourceViewer.ZoomFactor - SourceViewer.ViewportWidth / 2), Math.Max(0, point.Y * SourceViewer.ZoomFactor - SourceViewer.ViewportHeight / 2), null, true);
            }
            catch { ViewModel.StatusMessage = UiText.Get("Could not locate the original. Reopen the material."); }
            return;
        }
        ViewModel.StatusMessage = UiText.Get("This question has no corresponding source location.");
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
            ViewModel.StatusMessage = UiText.Get("Select an available library in Settings before importing.");
            Navigation.SelectedItem = Navigation.SettingsItem; return;
        }
        _openingFile = true;
        try
        {
            if (_library is not null) await PickAndImportAsync();
            else
            {
                var files = await PickFilesAsync();
                if (files.Count > 0) await PreviewFileAsync(files[0]);
            }
        }
        catch (Exception exception)
        {
            AppLog.Error("OpenFile", exception);
            ViewModel.StatusMessage = UiText.Get("Could not open the file. Select it again.");
        }
        finally { _openingFile = false; }
    }

    private void Source_DragOver(object sender, DragEventArgs e)
    {
        if (e.DataView.Contains(StandardDataFormats.StorageItems))
        {
            e.AcceptedOperation = DataPackageOperation.Copy;
            e.DragUIOverride.Caption = UiText.Get("Import image or PDF");
        }
    }

    private async void Source_Drop(object sender, DragEventArgs e)
    {
        if (!e.DataView.Contains(StandardDataFormats.StorageItems)) return;
        if (_library is null && !ViewModel.IsSample)
        {
            ViewModel.StatusMessage = UiText.Get("Select an available library in Settings before importing.");
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
                ViewModel.StatusMessage = UiText.Get("Drop one file at a time.");
                return;
            }
            await PreviewFileAsync(WinRT.CastExtensions.As<StorageFile>(items[0]));
        }
        catch (Exception exception)
        {
            AppLog.Error("DropFile", exception);
            ViewModel.StatusMessage = UiText.Get("Could not open the file. Select it again.");
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
            ViewModel.StatusMessage = UiText.Get("Choose a PNG, JPEG, WebP image, or PDF.");
            return;
        }
        CancelPreviewLoad();
        long generation = _previewGeneration;
        var properties = await file.GetBasicPropertiesAsync();
        if (_closed || generation != _previewGeneration) return;
        if (properties.Size > 64UL * 1024 * 1024)
        {
            ViewModel.StatusMessage = UiText.Get("File too large. Choose an image under 64 MB.");
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
            ViewModel.StatusMessage = UiText.Get("Image too large. Resize or crop it and retry.");
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
        WorkspaceNotice.Title = UiText.Get("Sample questions");
        WorkspaceNotice.Message = UiText.Get("Unrelated to the current original. Changes will not be saved.");
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
        ViewModel.StatusMessage = UiText.Get("Loading canceled.");
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
        PdfPageLabel.Text = loading ? UiText.Get("Loading page…") : UiText.Format("Page {0} / {1}", _pdfPageIndex + 1, _pdfPageCount);
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
        ViewModel.StatusMessage = UiText.Get("Opening…");
        try
        {
            var rendered = await Task.Run(() =>
            {
                cancellation.Token.ThrowIfCancellationRequested();
                var file = new FileInfo(Path.GetFullPath(path));
                if (!file.Exists || file.Length is <= 0 or > 64L * 1024 * 1024)
                    throw new PreviewInputException(UiText.Get("Choose a nonempty PDF no larger than 64 MB."));
                var rasterizer = new PdfiumRasterizer(Path.Combine(AppContext.BaseDirectory, "native", "pdfium.dll"));
                using var document = rasterizer.Open(file.FullName);
                int count = document.PageCount;
                if (count is < 1 or > 12) throw new PreviewInputException(UiText.Get("Choose a PDF with 1 to 12 pages."));
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
            WorkspaceNotice.Title = UiText.Get("Sample questions");
            WorkspaceNotice.Message = UiText.Get("Unrelated to the current original. Changes will not be saved.");
            UpdateSourceHighlight();
            FitPage();
            ViewModel.StatusMessage = "";
            return true;
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            if (!_closed && generation == _previewGeneration) ViewModel.StatusMessage = UiText.Get("Loading canceled.");
            return false;
        }
        catch (PreviewInputException exception)
        {
            if (!_closed && generation == _previewGeneration) ViewModel.StatusMessage = exception.Message;
            return false;
        }
        catch (Exception exception)
        {
            AppLog.Error("PreviewPdf", exception);
            if (!_closed && generation == _previewGeneration) ViewModel.StatusMessage = UiText.Get("Could not open this PDF. Check that it is complete or try another file.");
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
            throw new InvalidDataException(UiText.Get("Invalid PDF raster row layout. "));
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
        if (width <= 0 || height <= 0) throw new InvalidDataException(UiText.Get("Invalid page dimensions. "));
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
        WorkspaceNotice.Title = UiText.Get("Sample materials"); WorkspaceNotice.Message = UiText.Get("You can view and edit this sample. Changes will not be saved."); WorkspaceNotice.IsOpen = true;
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
        WorkspaceNotice.Title = UiText.Get("Sample materials");
        WorkspaceNotice.Message = UiText.Get("You can view and edit this sample. Changes will not be saved.");
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

    private async void About_Click(object sender, RoutedEventArgs e) => await ShowAboutAndUpdatesAsync();

}
