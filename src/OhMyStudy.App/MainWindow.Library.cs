using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Services;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.ViewModels;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Documents;
using OhMyStudy.Infrastructure.Credentials;
using OhMyStudy.Infrastructure.Execution;
using OhMyStudy.Infrastructure.Grading;
using OhMyStudy.Infrastructure.Jobs;
using OhMyStudy.Infrastructure.Providers;
using OhMyStudy.Infrastructure.Storage;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private LibraryRepository? _library;
    private JobQueue? _jobs;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly ObservableCollection<Document> _documents = [];
    private readonly ObservableCollection<Provider> _providers = [];
    private readonly SemaphoreSlim _importGate = new(1, 1);
    private readonly SemaphoreSlim _settingsGate = new(1, 1);
    private bool _loadingLibrary;
    private string _libraryDirectory = "";
    private string? _libraryPointerOverride;
    private string? _activeJob;
    private long _documentGeneration;
    private int _sourcePageIndex;
    private string? _sourcePageId;
    private bool _restoringRotation;
    private bool _shutdownStarted;
    private bool _shutdownFinished;
    private bool _closeReviewOpen;
    private long _taskRefreshGeneration;
    private AppSettings _settings = AppSettings.Default;
    private string? _selectedProviderId;
    private readonly BoundedRasterPageCache _pageCache = new(64L * 1024 * 1024, 16);
    private Dictionary<string, (int Page, int Rotation, float Zoom, double X, double Y)> _viewports = new(StringComparer.Ordinal);

    private async void AppWindow_Closing(Microsoft.UI.Windowing.AppWindow sender, Microsoft.UI.Windowing.AppWindowClosingEventArgs args)
    {
        if (_shutdownFinished || _library is null && _jobs is null) return;
        args.Cancel = true;
        if (_shutdownStarted || _closeReviewOpen) return;
        if (_questionEditorOpen) { ViewModel.StatusMessage = UiText.Get("Save or close the editor first."); return; }
        _closeReviewOpen = true;
        try { if (!await ConfirmDiscardEditsAsync(true)) return; }
        catch { ViewModel.StatusMessage = UiText.Get("Close the current dialog before exiting."); return; }
        finally { _closeReviewOpen = false; }
        _shutdownStarted = true;
        _lifetime.Cancel();
        ViewModel.StatusMessage = UiText.Get("Stopping tasks…");
        try { if (_jobs is not null) await _jobs.DisposeAsync(); }
        finally
        {
            if (_library is not null) await _library.DisposeAsync();
            _shutdownFinished = true;
            Close();
        }
    }

    private async Task RotateSourceAsync(int delta)
    {
        Rotate(delta);
        if (_restoringRotation || _library is null || ViewModel.IsSample || ViewModel.CurrentDocument is not { } document || _sourcePageId is null) return;
        string sourceId = _sourcePageId;
        long generation = _documentGeneration;
        int oldRotation = document.Pages.First(p => p.Id == sourceId).Rotation;
        try
        {
            var edits = document.Pages.Select(p => new PageEdit { Id = p.Id, Rotation = p.Id == sourceId ? (ushort)_rotation : p.Rotation }).ToArray();
            var updated = await _library.UpdatePagesAsync(document.Id, edits, _lifetime.Token);
            var selected = ViewModel.SelectedQuestion.Saved?.Id;
            var questions = await _library.GradingQuestionsAsync(document.Id, _lifetime.Token);
            if (_closed || generation != _documentGeneration || ViewModel.CurrentDocument?.Id != document.Id || _sourcePageId != sourceId) return;
            ViewModel.LoadDocument(updated, questions);
            if (selected is not null && ViewModel.Questions.FirstOrDefault(q => q.Saved?.Id == selected) is { } question) ViewModel.SelectedQuestion = question;
        }
        catch { if (!_closed && generation == _documentGeneration && _sourcePageId == sourceId) { _rotation = oldRotation; Rotate(0); ViewModel.StatusMessage = UiText.Get("Rotation could not be saved. Wait for the task to finish and retry."); } }
    }

    private async Task<bool> InitializeLibraryAsync(string? directory = null)
    {
        if (_loadingLibrary) return false;
        _loadingLibrary = true;
        bool complete = false;
        try
        {
            string appData = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMyStudy");
            var pointer = _libraryPointerOverride ?? Path.Combine(appData, "active-library.txt");
            var selected = directory is null && File.Exists(pointer) ? (await File.ReadAllTextAsync(pointer, _lifetime.Token)).Trim() : null;
            _libraryDirectory = directory ?? selected ?? Path.Combine(appData, "Library");
            LibraryPath.Text = _libraryDirectory;
            if (selected is not null && (!Path.IsPathFullyQualified(selected) || !File.Exists(LegacyLibraryLocation.DatabaseFile(selected))))
                throw new IOException(UiText.Get("The save location is inaccessible."));
            var mode = File.Exists(LegacyLibraryLocation.DatabaseFile(_libraryDirectory)) ? LibraryOpenMode.ReadWrite : LibraryOpenMode.CreateNew;
            _library = await LibraryRepository.OpenAsync(new(_libraryDirectory, mode) { MediaProcessor = new NativeMediaProcessor() }, _lifetime.Token);
            LibraryPath.Text = _libraryDirectory;
            string privateRuntime = Path.Combine(AppContext.BaseDirectory, "runtime", "python");
            var activeLibrary = _library;
            _jobs = await JobQueue.CreateAsync(new LibraryJobStore(_library), new LoggedJobRunner(new GradingRunner(_library, new NativePageImages())),
                new PolicyToolExecutor(new HostToolExecutor(privateRuntime), new SandboxToolExecutor(privateRuntime)), Path.Combine(_libraryDirectory, "task-results"), _lifetime.Token);
            ViewModel.SaveQuestionAsync = async question =>
            {
                var submitted = question.EditedQuestion;
                question.AcceptSaved(await activeLibrary.EditQuestionAsync(question.Saved!.Id, submitted, _lifetime.Token), submitted);
                // A later read failure cannot turn a committed save into an unsaved draft.
                try { await ReloadQuestionsAsync(); } catch { }
            };
            ViewModel.CancelCurrentJob = async () =>
            {
                if (_activeJob is { } id && _jobs is not null)
                    try { await _jobs.CancelAsync(id); await RefreshTasksAsync(); } catch { ViewModel.StatusMessage = UiText.Get("Cancellation did not finish. Try again later."); }
            };
            PaperList.ItemsSource = _filteredPapers;
            ProviderPicker.ItemsSource = _providers;
            _settings = await _library.GetSettingsAsync(_lifetime.Token);
            ExecutionModePicker.SelectedIndex = _settings.ExecutionMode switch { "sandbox" => 1, "bypass_permission" => 2, _ => 0 };
            SandboxNetworkToggle.IsChecked = _settings.SandboxNetworking;
            await RefreshProvidersAsync();
            await RefreshDocumentsAsync();
            _ = ObserveJobsAsync();
            await RefreshTasksAsync();
            if (_documents.Count == 0)
            {
                ViewModel.ClearLibrary(); OriginalCaption.Text = UiText.Get("Imported materials will appear here.");
                WorkspaceNotice.IsOpen = false;
            }
            string legacy = LegacyLibraryLocation.ResolveDefault(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            if (directory is null && !_settings.LegacyNoticeDismissed && File.Exists(LegacyLibraryLocation.DatabaseFile(legacy)) && _documents.Count == 0)
            {
                WorkspaceNotice.Title = UiText.Get("Legacy materials found");
                WorkspaceNotice.Message = UiText.Get("Import them from the More menu. The originals will be preserved."); WorkspaceNotice.IsOpen = true;
            }
            complete = true;
            ViewModel.StatusMessage = "";
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch (Exception error) { AppLog.Error("Library.Open", error); ViewModel.StatusMessage = UiText.Get("Could not open the library. Check its location or select an existing library in Settings."); GradeButton.IsEnabled = false; return false; }
        finally
        {
            if (!complete)
            {
                try { await DisposeLibrarySessionAsync(); } catch { }
                ViewModel.SaveQuestionAsync = null; ViewModel.CancelCurrentJob = null;
                _documents.Clear(); _providers.Clear(); _papers.Clear(); ApplyPaperFilters();
                ViewModel.ClearLibrary(); ReleaseReadingResources();
            }
            _loadingLibrary = false;
        }
    }

    private async Task DisposeLibrarySessionAsync()
    {
        var jobs = _jobs; var library = _library;
        _jobs = null; _library = null;
        try { if (jobs is not null) await jobs.DisposeAsync(); }
        finally { if (library is not null) await library.DisposeAsync(); }
    }

    private async Task RefreshDocumentsAsync(string? selectId = null)
    {
        if (_library is null) return;
        long generation = ++_documentListGeneration;
        var summaries = await _library.ListPapersAsync(_lifetime.Token);
        if (_closed || generation != _documentListGeneration) return;
        _documents.Clear(); foreach (var item in summaries) _documents.Add(item.Document);
        _papers.Clear(); _papers.AddRange(summaries.Select(item => new PaperViewModel(item)));
        RefreshPaperSubjects(); ApplyPaperFilters();
        string? selected = selectId ?? ViewModel.CurrentDocument?.Id;
        if ((selectId is not null || WorkspaceGrid.Visibility == Visibility.Visible && ViewModel.SectionTitle == UiText.Get("Exams")) && selected is not null && _documents.FirstOrDefault(d => d.Id == selected) is { } document)
            await LoadDocumentAsync(document);
    }

    private async Task LoadDocumentAsync(Document document)
    {
        if (_library is null) return;
        RememberViewport();
        ShowMaterialsNavigation();
        long generation = ++_documentGeneration;
        var current = await _library.GetDocumentAsync(document.Id, _lifetime.Token);
        var result = await _library.GradingResultAsync(document.Id, _lifetime.Token);
        if (_closed || generation != _documentGeneration) return;
        ViewModel.LoadDocument(current, result.Questions);

        GradeButton.IsEnabled = current.Pages.Count > 0 && !current.PendingPdfPages;
        WorkspaceNotice.IsOpen = current.PendingPdfPages;
        WorkspaceNotice.Title = UiText.Get("Material is not ready");
        WorkspaceNotice.Message = UiText.Get("Reopen or import the file and retry.");
        var view = _viewports.GetValueOrDefault(current.Id);
        _sourcePageIndex = Math.Clamp(view.Page, 0, Math.Max(0, current.Pages.Count - 1));
        await ShowSourcePageAsync(_sourcePageIndex);
        if (_closed || generation != _documentGeneration || ViewModel.CurrentDocument?.Id != current.Id) return;
        if (view.Zoom > 0) SourceViewer.ChangeView(view.X, view.Y, view.Zoom, true);
        await RestoreWorkspaceContextAsync("materials:" + current.Id, generation);
        UpdateSourceHighlight();
        UpdateQuestionState();
        ApplyWorkspaceLayout();
    }

    private async Task ShowQuestionLibraryAsync()
    {
        RememberViewport();
        long generation = ++_documentGeneration;
        CancelPreviewLoad(); SourcePane.Visibility = Visibility.Collapsed;
        _suppressNavigation = true;
        try { Navigation.SelectedItem = Navigation.MenuItems[1]; }
        finally { _suppressNavigation = false; }
        SettingsPanel.Visibility = TasksPanel.Visibility = DocumentToolbar.Visibility = SectionCaption.Visibility = Visibility.Collapsed;
        WorkspaceGrid.Visibility = Visibility.Visible; WorkspaceActions.Visibility = PapersPanel.Visibility = Visibility.Collapsed;
        WorkspaceNotice.Visibility = Visibility.Collapsed;
        ViewModel.ShowSection("library");
        if (_library is not null)
        {
            var questions = await _library.ListQuestionsAsync(_lifetime.Token);
            if (_closed || generation != _documentGeneration) return;
            ViewModel.LoadLibrary(questions, false);
            await RestoreWorkspaceContextAsync("library", generation);
        }
        UpdateQuestionState();
        ApplyWorkspaceLayout();
    }

    private void RememberViewport()
    {
        RememberWorkspaceContext();
        if (ViewModel.CurrentDocument is { } current)
            _viewports[current.Id] = (_sourcePageIndex, _rotation, SourceViewer.ZoomFactor, SourceViewer.HorizontalOffset, SourceViewer.VerticalOffset);
    }

    private async Task ReloadQuestionsAsync()
    {
        if (_library is null || _closed) return;
        long generation = _documentGeneration;
        long reload = ++_questionReloadGeneration;
        RememberWorkspaceContext();
        string contextKey = _workspaceContextKey;
        if (ViewModel.CurrentDocument is { } document)
        {
            var result = await _library.GradingResultAsync(document.Id, _lifetime.Token);
            if (_closed || generation != _documentGeneration || reload != _questionReloadGeneration || ViewModel.CurrentDocument?.Id != document.Id) return;
            ViewModel.LoadDocument(document, result.Questions);
        }
        else if (!ViewModel.IsSample)
        {
            var questions = await _library.ListQuestionsAsync(_lifetime.Token);
            if (_closed || generation != _documentGeneration || reload != _questionReloadGeneration) return;
            ViewModel.LoadLibrary(questions, ViewModel.SectionTitle == UiText.Get("Mistakes"));
        }
        await RestoreWorkspaceContextAsync(contextKey, generation);
    }

    private async Task ShowSourcePageAsync(int index)
    {
        if (_library is null || ViewModel.CurrentDocument is not { } document || index < 0 || index >= document.Pages.Count) return;
        var library = _library;
        RememberPageViewport();
        var ct = BeginReadingLoad(); long generation = _previewGeneration;
        var page = document.Pages[index];
        try
        {
            int level = _pageViewports.TryGetValue(page.Id, out var savedView) && savedView.Zoom > 1 ? 2400 : 1024;
            var raster = await GetReadingRasterAsync(library, page, level, ct);
            if (_closed || generation != _previewGeneration) return;
            var bitmap = await CreateRasterImageAsync(raster);
            if (_closed || generation != _previewGeneration) return;
            _settingRaster = true;
            try
            {
                ImportedImage.Source = bitmap; ImportedImage.Visibility = Visibility.Visible; SamplePaper.Visibility = Visibility.Collapsed;
                _currentRasterLevel = level;
                _hasImportedImage = true; _pdfPath = null; _sourcePageIndex = index; _sourcePageId = page.Id;
                _restoringRotation = true; _rotation = page.Rotation;
                // Keep one geometry at every resolution so zoom upgrades preserve the source overlay.
                SetPageDimensions(page.Width ?? (uint)raster.Width, page.Height ?? (uint)raster.Height); _restoringRotation = false;
                OriginalCaption.Text = page.Role == "reference" ? UiText.Get("Reference answers") : "";
                PdfPageControls.Visibility = document.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                PdfPageLabel.Text = UiText.Format("Page {0} / {1}", index + 1, document.Pages.Count);
                PreviousPdfPage.IsEnabled = index > 0; NextPdfPage.IsEnabled = index + 1 < document.Pages.Count;
                CancelPreview.Visibility = Visibility.Collapsed;
                if (_pageViewports.TryGetValue(page.Id, out savedView)) SourceViewer.ChangeView(savedView.X, savedView.Y, savedView.Zoom, true);
                else FitPage();
                UpdateSourceHighlight();
            }
            finally { _settingRaster = false; }
            StartReadingPrefetch(document, index);
            ImproveReadingResolutionAsync();
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch (Exception error) { AppLog.Error("Document.ShowPage", error); if (!_closed && generation == _previewGeneration) ViewModel.StatusMessage = UiText.Get("Could not open this page. Select or import it again."); }
    }

    private async Task ImportFilesAsync(IReadOnlyList<StorageFile> files, bool reference = false, string? referenceDocumentId = null, ProcessingChoice? confirmedProcessing = null)
    {
        if (_library is null || files.Count == 0) return;
        if (!await _importGate.WaitAsync(0)) return;
        var clock = Stopwatch.StartNew();
        Document? imported = null;
        bool recognitionQueued = false;
        Job? recognitionJob = null;
        ImportPaperButton.IsEnabled = false;
        try
        {
            if (HasUnsavedSettingsChanges)
            {
                if (!await ConfirmDiscardSettingsAsync()) return;
                DiscardSettingsForm();
            }
            if (files.Count > LibraryLimits.MaximumPages) { ViewModel.StatusMessage = UiText.Get("Import up to 12 pages at a time."); return; }
            var processing = reference ? null : confirmedProcessing ?? await ChooseProcessingAsync(false, importing: true);
            if (!reference && processing is null) return;
            ViewModel.StatusMessage = UiText.Get("Importing…");
            AppLog.Info("Import.Start", $"Files={files.Count}; Reference={reference}");
            var sources = new List<ImportSource>();
            long total = 0;
            foreach (var file in files)
            {
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size > LibraryLimits.MaximumAssetBytes || total + (long)properties.Size > LibraryLimits.MaximumBatchBytes)
                    throw new InvalidDataException(UiText.Get("Files must be at most 64 MB each and 256 MB per import. Split them and retry."));
                AppLog.Info("Import.ReadFile", $"Type={file.FileType}; Bytes={properties.Size}");
                byte[] bytes = await File.ReadAllBytesAsync(file.Path, _lifetime.Token);
                total += bytes.Length; sources.Add(new(file.Name, bytes, reference ? "reference" : "material"));
            }
            imported = reference && referenceDocumentId is not null
                ? await _library.AppendDocumentSourcesAsync(referenceDocumentId, sources, _lifetime.Token)
                : await _library.ImportDocumentAsync(Path.GetFileNameWithoutExtension(files[0].Name), processing!.Mode, sources, _lifetime.Token);
            AppLog.Info("Import.Saved", $"Document={imported.Id}; Pages={imported.Pages.Count}; ElapsedMs={clock.ElapsedMilliseconds}");
            await RefreshDocumentsAsync(imported.Id);
            if (!reference)
            {
                recognitionJob = await SubmitProcessingAsync(imported, processing!, false);
                recognitionQueued = true;
                await ShowQuestionLibraryAsync();
            }
            ViewModel.StatusMessage = reference ? UiText.Get("Reference answers added. You can organize them again.")
                : RecognitionStatus((await _jobs!.GetAsync(recognitionJob!.Id, _lifetime.Token)).State);
        }
        catch (OperationCanceledException) { ViewModel.StatusMessage = UiText.Get("Import canceled."); }
        catch (Exception error)
        {
            AppLog.Error(imported is null ? "Import.Prepare" : "Import.StartRecognition", error);
            ViewModel.StatusMessage = recognitionQueued ? UiText.Get("Exam imported and recognition started. Reopen the question bank to refresh it.")
                : imported is not null ? UiText.Get("Exam saved, but recognition could not start. Start recognition again in Exams.") : ImportFailureMessage(error);
        }
        finally { ImportPaperButton.IsEnabled = true; _importGate.Release(); }
    }

    private static string ImportFailureMessage(Exception error) => error switch
    {
        FileNotFoundException or DllNotFoundException or BadImageFormatException => UiText.Get("Import components are missing or damaged. Reinstall the app and retry."),
        PdfReadException => UiText.Get("Import failed: ") + error.Message,
        InvalidDataException when error.Message.Contains("PDFium", StringComparison.Ordinal) => UiText.Get("Could not process the PDF. Reinstall the app and retry."),
        UnauthorizedAccessException => UiText.Get("Could not read the file or save materials. Check access permissions and retry."),
        InvalidDataException => UiText.Get("Import failed: ") + error.Message,
        IOException => UiText.Get("Could not read or save the exam. Check the file and save location."),
        _ => UiText.Get("Import failed. Select the file again and retry.")
    };

    private async Task<IReadOnlyList<StorageFile>> PickFilesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".pdf" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return await picker.PickMultipleFilesAsync();
    }
    private async void AddReferences_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } document) { ViewModel.StatusMessage = UiText.Get("Select a material first."); return; }
        try { await ImportFilesAsync(await PickFilesAsync(), true, document.Id); } catch { ViewModel.StatusMessage = UiText.Get("Could not open the file. Try again."); }
    }

    private async Task RefreshProvidersAsync()
    {
        if (_library is null) return;
        var providers = await _library.ListProvidersAsync(_lifetime.Token);
        _refreshingProviders = true;
        try
        {
            _providers.Clear(); foreach (var provider in providers) _providers.Add(provider);
            ProviderPicker.SelectedItem = _providers.FirstOrDefault(p => p.Id == _selectedProviderId) ?? _providers.FirstOrDefault();
        }
        finally { _refreshingProviders = false; }
        if (ProviderPicker.SelectedItem is Provider selectedProvider) PopulateProviderEditor(selectedProvider);
        else ClearProviderEditor();
    }
    private async void ProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_refreshingProviders || ProviderPicker.SelectedItem is not Provider provider) return;
        if (HasUnsavedSettingsChanges)
        {
            _refreshingProviders = true;
            try { ProviderPicker.SelectedItem = _providers.FirstOrDefault(p => p.Id == _selectedProviderId); }
            finally { _refreshingProviders = false; }
            if (!await ConfirmDiscardSettingsAsync()) return;
            _refreshingProviders = true;
            try { ProviderPicker.SelectedItem = provider; }
            finally { _refreshingProviders = false; }
        }
        PopulateProviderEditor(provider);
    }
    private void PopulateProviderEditor(Provider provider)
    {
        _selectedProviderId = provider.Id; ProviderName.Text = provider.Name; ProviderUrl.Text = provider.BaseUrl;
        ProtocolPicker.SelectedIndex = (int)provider.Protocol; ProviderKey.Password = "";
        ProviderFeedback.Text = "";
        UpdateModelList(provider);
        ModelFeedback.Text = provider.Models.Count == 0 ? UiText.Get("No models yet. Fetch the list or add one manually.") : "";
        _providerFormBaseline = ProviderFormState(); _settingsFormsReady = true;
    }
    private async void NewProvider_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmDiscardSettingsAsync()) ClearProviderEditor();
    }
    private void ClearProviderEditor()
    {
        ProviderPicker.SelectedIndex = -1; _selectedProviderId = null; ProviderName.Text = ""; ProviderUrl.Text = ""; ProviderKey.Password = ""; ProviderFeedback.Text = "";
        ProtocolPicker.SelectedIndex = 0;
        _modelSelectionSuppressed = true;
        try { ModelPicker.ItemsSource = null; }
        finally { _modelSelectionSuppressed = false; }
        ModelEditor.Visibility = Visibility.Collapsed; ModelFeedback.Text = UiText.Get("Save the provider before adding models.");
        ModelPicker.Visibility = Visibility.Collapsed;
        UpdateSettingsActionAvailability();
        _providerFormBaseline = ProviderFormState(); _modelFormBaseline = ModelFormState(); _settingsFormsReady = true;
    }
    private async void SaveProvider_Click(object sender, RoutedEventArgs e) => await SaveProviderAsync();

    private async Task SaveProviderAsync()
    {
        if (_library is null) { ProviderFeedback.Text = UiText.Get("Select an available library first."); return; }
        if (_modelRequestRunning) return;
        _modelRequestRunning = true; SettingsPanel.IsEnabled = false;
        byte[]? key = null;
        try
        {
            var old = _providers.FirstOrDefault(p => p.Id == _selectedProviderId);
            var protocol = (Protocol)ProtocolPicker.SelectedIndex;
            using var validation = new ProviderClient(protocol, ProviderUrl.Text.Trim());
            var models = old?.Models.ToList() ?? [];
            if (old is not null && old.Protocol != protocol && models.Count > 0)
                throw new InvalidOperationException(UiText.Get("A provider with existing models cannot change protocol. Add a new provider."));
            var provider = new Provider
            {
                Id = _selectedProviderId ?? Guid.NewGuid().ToString(),
                Name = ProviderName.Text.Trim(),
                Protocol = protocol,
                BaseUrl = ProviderUrl.Text.Trim(),
                Models = models
            };
            if (!string.IsNullOrEmpty(ProviderKey.Password)) key = Encoding.UTF8.GetBytes(ProviderKey.Password);
            var saved = await new ProviderCredentialService(_library, new WindowsCredentialVault()).SaveAsync(provider, key is null ? null : key.AsMemory(), _lifetime.Token);
            _selectedProviderId = saved.Provider.Id; ProviderKey.Password = "";
            UpdateProviderEntry(saved.Provider); _providerFormBaseline = ProviderFormState();
            if (saved.Provider.Models.Count == 0 && ModelEditor.Visibility == Visibility.Collapsed)
                ModelFeedback.Text = UiText.Get("No models yet. Fetch the list or add one manually.");
            ProviderFeedback.Text = saved.CredentialCleanupRequired ? UiText.Get("Saved, but the old key could not be removed. Save again later.") : UiText.Get("Saved.");
        }
        catch (InvalidOperationException error) when (error.Message.StartsWith(UiText.Get("A provider with existing models"), StringComparison.Ordinal)) { ProviderFeedback.Text = error.Message; }
        catch { ProviderFeedback.Text = UiText.Get("Could not save. Check the name, address, and key."); }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    private async void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null || _selectedProviderId is null) return;
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = UiText.Get("Delete provider?"), Content = UiText.Get("Existing materials and grading records will be preserved."), PrimaryButtonText = UiText.Get("Delete"), CloseButtonText = UiText.Get("Cancel") };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        _modelRequestRunning = true; SettingsPanel.IsEnabled = false;
        try
        {
            var id = _selectedProviderId;
            var cleanupRequired = await new ProviderCredentialService(_library, new WindowsCredentialVault()).DeleteAsync(id, _lifetime.Token);
            if (_settings.DefaultModel?.StartsWith(id + ":", StringComparison.Ordinal) == true)
                await ChangeSettingsAsync(current => current with { DefaultModel = null });
            ClearProviderEditor(); await RefreshProvidersAsync();
            ProviderFeedback.Text = cleanupRequired ? UiText.Get("Provider deleted, but its key could not be removed.") : UiText.Get("Deleted.");
        }
        catch { ProviderFeedback.Text = UiText.Get("Could not delete. Try again later."); }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; }
    }
    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        if (_modelRequestRunning) return;
        if (_library is null || ProviderPicker.SelectedItem is not Provider provider) return;
        if (!await ConfirmDiscardSettingsAsync(modelOnly: true)) return;
        _modelRequestRunning = true; SettingsPanel.IsEnabled = false;
        byte[]? key = null;
        try
        {
            key = WindowsCredentialStore.Read(provider.KeyRef ?? "");
            if (key is null) { ProviderFeedback.Text = UiText.Get("Save the key first."); return; }
            using var client = new ProviderClient(provider.Protocol, provider.BaseUrl);
            var models = new Dictionary<string, ModelConfig>(StringComparer.Ordinal);
            string? cursor = null;
            var visited = new HashSet<string>(StringComparer.Ordinal);
            do
            {
                var page = await client.ListModelsAsync(new(Encoding.UTF8.GetString(key)), cursor, _lifetime.Token);
                foreach (var model in page.Models) if (!models.ContainsKey(model.Id)) models[model.Id] = new() { Id = model.Id, Capabilities = model.Capabilities, Currency = "USD" };
                cursor = page.Next;
                if (models.Count > 10000 || cursor is not null && (!visited.Add(cursor) || visited.Count > 100)) throw new InvalidDataException(UiText.Get("The model list is too large or contains repeated pages."));
            } while (cursor is not null);
            var candidates = models.Values.Where(m => !provider.Models.Any(old => old.Id == m.Id)).OrderBy(m => m.Label, StringComparer.CurrentCultureIgnoreCase).ToList();
            if (candidates.Count == 0) { ProviderFeedback.Text = UiText.Get("No new models to add."); return; }
            var selection = CreateModelImportDialog(candidates);
            if (await selection.Dialog.ShowAsync() != ContentDialogResult.Primary) { ProviderFeedback.Text = ""; return; }
            var picked = candidates.Where(m => selection.Selected.Contains(m.Id)).ToList();
            if (picked.Count == 0) return;
            if (provider.Models.Count + picked.Count > 500) { ProviderFeedback.Text = UiText.Get("Each provider supports up to 500 models. Select fewer models."); return; }
            var updated = await _library.UpsertProviderAsync(provider with { Models = [.. provider.Models, .. picked] }, _lifetime.Token);
            UpdateProviderEntry(updated); UpdateModelList(updated); ProviderFeedback.Text = UiText.Format("Models added: {0}.", picked.Count);
        }
        catch { ProviderFeedback.Text = UiText.Get("Fetch failed. Check the address, key, and network."); }
        finally { _modelRequestRunning = false; SettingsPanel.IsEnabled = true; if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private async void ExecutionModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || ExecutionDescription is null) return;
        var mode = ExecutionModePicker.SelectedIndex switch { 1 => "sandbox", 2 => "bypass_permission", _ => "request_permission" };
        ExecutionDescription.Text = mode == "sandbox" ? UiText.Get("You will be informed before starting if the sandbox is unavailable.") : mode == "request_permission" ? UiText.Get("You decide whether to allow each code or command execution.") : UiText.Get("Code and commands can access this account's files and network.");
        if (_loadingLibrary || _library is null) return;
        try { await ChangeSettingsAsync(current => current with { ExecutionMode = mode }); } catch { ViewModel.StatusMessage = UiText.Get("Settings could not be saved."); }
    }
    private async void Grade_Click(object sender, RoutedEventArgs e) => await StartGradingAsync(false);
    private async void SandboxNetworkToggle_Changed(object sender, RoutedEventArgs e)
    {
        if (!_initialized || _loadingLibrary || _library is null) return;
        bool enabled = SandboxNetworkToggle.IsChecked == true;
        try { await ChangeSettingsAsync(current => current with { SandboxNetworking = enabled }); }
        catch { ViewModel.StatusMessage = UiText.Get("Settings could not be saved. Try again."); }
    }
    private async Task ChangeSettingsAsync(Func<AppSettings, AppSettings> change)
    {
        var library = _library ?? throw new IOException(UiText.Get("Open a library first."));
        await _settingsGate.WaitAsync(_lifetime.Token);
        try
        {
            if (!ReferenceEquals(library, _library)) throw new OperationCanceledException();
            var saved = await library.SaveSettingsAsync(change(_settings), _lifetime.Token);
            if (ReferenceEquals(library, _library)) _settings = saved;
        }
        finally { _settingsGate.Release(); }
    }
    private async void ReadReferences_Click(object sender, RoutedEventArgs e) => await StartGradingAsync(true);

    private async Task StartGradingAsync(bool references)
    {
        if (_library is null || _jobs is null || ViewModel.CurrentDocument is not { } document) return;
        try
        {
            var processing = await ChooseProcessingAsync(references, importing: false);
            if (processing is null) return;
            await SubmitProcessingAsync(document, processing, references);
            ViewModel.StatusMessage = UiText.Get("Added to queue.");
        }
        catch (Exception error) { AppLog.Error("Recognition.Start", error); ViewModel.StatusMessage = UiText.Get("Could not start the task. Check model settings or wait for the current task to finish."); }
    }

    private sealed record ProcessingChoice(Provider Provider, ModelConfig Model, Thinking? Thinking, bool AllowUnknown, ExecutionPolicy Policy, ImportMode Mode);

    private async Task<ProcessingChoice?> ChooseProcessingAsync(bool references, bool importing)
    {
        if (_library is null || _jobs is null) return null;
        var providers = await _library.ListProvidersAsync(_lifetime.Token);
        providers = providers.Where(p => p.KeyRef is not null && p.Models.Count > 0).ToList();
        var provider = providers.FirstOrDefault(p => p.Models.Any(m => p.Id + ":" + m.Id == _settings.DefaultModel)) ?? providers.FirstOrDefault(p => p.Models.Any(m => m.Id == _settings.DefaultModel)) ?? providers.FirstOrDefault();
        if (provider is null || provider.Models.Count == 0 || provider.KeyRef is null)
        {
            var setup = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.ActualTheme, Title = UiText.Get("Set up a recognition model first"), Content = UiText.Get("Recognition requires a model and key. Once set up, import exams to add questions automatically."), PrimaryButtonText = UiText.Get("Go to Settings"), CloseButtonText = UiText.Get("Cancel"), DefaultButton = ContentDialogButton.Primary };
            if (await setup.ShowAsync() == ContentDialogResult.Primary) Navigation.SelectedItem = Navigation.SettingsItem;
            ViewModel.StatusMessage = importing ? UiText.Get("Not imported yet. Set up a model first.") : UiText.Get("Add a model and key in Settings first.");
            return null;
        }
        var model = provider.Models.FirstOrDefault(m => provider.Id + ":" + m.Id == _settings.DefaultModel || m.Id == _settings.DefaultModel) ?? provider.Models[0];
        var availableModels = providers.SelectMany(p => p.Models.Select(m => (Provider: p, Model: m))).ToList();
        double modelWidth = Math.Max(200, Math.Min(460, RootGrid.ActualWidth - 96));
        var choice = CreateFixedModelChoice(availableModels.Select(a => a.Provider.Name + " / " + a.Model.Label), modelWidth);
        choice.SelectedIndex = availableModels.FindIndex(p => p.Provider.Id == provider.Id && p.Model.Id == model.Id);
        var thinkingChoice = new ComboBox { Header = UiText.Get("Reasoning"), HorizontalAlignment = HorizontalAlignment.Stretch, SelectedIndex = 0 };
        thinkingChoice.Items.Add(UiText.Get("Default")); foreach (var option in model.Capabilities.Thinking) thinkingChoice.Items.Add(ThinkingLabel(option));
        var unknown = new CheckBox { Content = UiText.Get("Allow trying models with unverified capabilities") };
        choice.SelectionChanged += (_, _) => { thinkingChoice.Items.Clear(); thinkingChoice.Items.Add(UiText.Get("Default")); if (choice.SelectedIndex >= 0) foreach (var option in availableModels[choice.SelectedIndex].Model.Capabilities.Thinking) thinkingChoice.Items.Add(ThinkingLabel(option)); thinkingChoice.SelectedIndex = 0; };
        var preferred = _settings.ExecutionMode switch { "sandbox" => ExecutionMode.Sandbox, "bypass_permission" => ExecutionMode.BypassPermission, _ => ExecutionMode.RequestPermission };
        var capability = await SandboxCapability.ProbeAsync(Path.Combine(AppContext.BaseDirectory, "runtime", "python"), _lifetime.Token);
        var policy = ExecutionPolicy.Freeze(preferred, capability, _settings.SandboxNetworking);
        var content = new StackPanel { Spacing = 14 }; content.Children.Add(choice); content.Children.Add(thinkingChoice); content.Children.Add(unknown);
        var modeChoice = new ComboBox { Header = UiText.Get("Exam type"), SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        modeChoice.Items.Add(UiText.Get("Blank exam")); modeChoice.Items.Add(UiText.Get("Answered, needs grading")); modeChoice.Items.Add(UiText.Get("Already graded by a teacher"));
        if (importing) content.Children.Insert(0, modeChoice);
        if (policy.Warning is not null) content.Children.Add(new TextBlock { Text = UiText.Get("Sandbox unavailable. Code and commands will run locally with access to this account's files and network."), TextWrapping = TextWrapping.Wrap });
        content.Children.Add(new TextBlock { Text = UiText.Get("Materials will be sent to the selected model and may incur charges."), TextWrapping = TextWrapping.Wrap });
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = importing ? UiText.Get("Import exam") : references ? UiText.Get("Organize reference answers") : UiText.Get("Recognize again"),
            RequestedTheme = RootGrid.ActualTheme,
            Content = content,
            PrimaryButtonText = importing ? UiText.Get("Import and recognize") : UiText.Get("Start"),
            CloseButtonText = UiText.Get("Cancel"),
            DefaultButton = ContentDialogButton.Primary
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        (provider, model) = availableModels[choice.SelectedIndex];
        Thinking? thinking = thinkingChoice.SelectedIndex > 0 ? model.Capabilities.Thinking[thinkingChoice.SelectedIndex - 1] : null;
        return new(provider, model, thinking, unknown.IsChecked == true, policy,
            modeChoice.SelectedIndex switch { 1 => ImportMode.AiGrading, 2 => ImportMode.TeacherGraded, _ => ImportMode.Blank });
    }

    private async Task<Job> SubmitProcessingAsync(Document document, ProcessingChoice processing, bool references)
    {
        if (_jobs is null) throw new InvalidOperationException(UiText.Get("Task queue is not ready."));
        var input = new GradingInput { Document = document, Provider = processing.Provider, Model = processing.Model, Thinking = processing.Thinking, AllowUnknown = processing.AllowUnknown };
        var submitted = await _jobs.SubmitAsync(references ? DiagnosticKind.References : DiagnosticKind.Grading, processing.Policy, input, _lifetime.Token);
        _activeJob = submitted.Id;
        AppLog.Info("Recognition.Queued", $"Job={_activeJob}; Document={document.Id}; Model={processing.Model.Id}");
        try { await RefreshTasksAsync(); }
        catch (Exception error) { AppLog.Error("Recognition.RefreshQueuedJob", error); }
        return submitted;
    }
    private static string RecognitionStatus(JobState state) => state switch
    {
        JobState.Completed => UiText.Get("Recognition complete. Questions added to the question bank."),
        JobState.Failed => UiText.Get("Recognition incomplete. Saved questions are preserved. Try recognition again in Exams."),
        JobState.Cancelled => UiText.Get("Recognition canceled. Saved questions are preserved."),
        _ => UiText.Get("Recognizing. Questions will join the question bank automatically.")
    };
    private static string ThinkingLabel(Thinking value) => value switch
    { ResponsesThinking v => v.Effort, AdaptiveThinking v => v.Effort, BudgetThinking v => $"{v.Tokens} tokens", _ => UiText.Get("Default") };

    private async Task ObserveJobsAsync()
    {
        if (_jobs is not { } observedJobs) return;
        try
        {
            await foreach (var change in observedJobs.WatchAsync(_lifetime.Token))
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (_closed || _lifetime.IsCancellationRequested || !ReferenceEquals(_jobs, observedJobs)) return;
                    try
                    {
                        AppLog.Info("Job.Changed", $"Job={change.JobId}; State={change.Kind}");
                        await RefreshTasksAsync();
                        if (change.Kind is "progress" or "completed" or "failed" or "cancelled") await ReloadQuestionsAsync();
                        if (PapersPanel.Visibility == Visibility.Visible && change.Kind is "completed" or "failed" or "cancelled") await RefreshDocumentsAsync();
                        if (change.Kind == "completed") ViewModel.StatusMessage = UiText.Get("Recognition complete. Questions added to the question bank.");
                        else if (change.Kind == "failed") ViewModel.StatusMessage = UiText.Get("Recognition incomplete. Saved questions are preserved. Try recognition again in Exams.");
                        else if (change.Kind == "cancelled") ViewModel.StatusMessage = UiText.Get("Recognition canceled. Saved questions are preserved.");
                    }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                    catch (Exception error) { AppLog.Error("Job.Refresh", error); if (!_closed) ViewModel.StatusMessage = UiText.Get("Task status could not refresh. Reopen Tasks."); }
                });
        }
        catch (OperationCanceledException) { }
        catch (Exception error) { AppLog.Error("Job.Observe", error); if (!_closed && ReferenceEquals(_jobs, observedJobs)) DispatcherQueue.TryEnqueue(() => ViewModel.StatusMessage = UiText.Get("Task status could not refresh.")); }
    }
    private async Task ReviewPermissionAsync(string id, PermissionReview review)
    {
        if (_jobs is null) return;
        var text = new TextBox { Text = review.Call.Content, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 340 };
        var task = new CheckBox { Content = UiText.Get("Also allow subsequent calls in this task") };
        var body = new StackPanel { Spacing = 12 }; body.Children.Add(new TextBlock { Text = UiText.Get("Runs locally with access to this account's files and network."), TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = UiText.Format("{0} · up to {1:0.###} seconds\nDirectory: {2}\nArguments: {3}", (review.Call.Tool == ToolKind.Python ? "Python" : "PowerShell"), review.Call.TimeoutMs / 1000d, review.Call.WorkingDirectory, DomainJson.Serialize(review.Call.Arguments)), TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        body.Children.Add(text); body.Children.Add(task);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, RequestedTheme = RootGrid.RequestedTheme, Title = UiText.Get("Allow execution?"), Content = body, PrimaryButtonText = UiText.Get("Allow"), SecondaryButtonText = UiText.Get("Deny"), CloseButtonText = UiText.Get("Later") };
        var result = await dialog.ShowAsync(); if (result == ContentDialogResult.None) return;
        try { await _jobs.DecideAsync(id, review.Digest, result == ContentDialogResult.Secondary ? PermissionChoice.Deny : task.IsChecked == true ? PermissionChoice.AllowTask : PermissionChoice.AllowOnce); await RefreshTasksAsync(); }
        catch { ViewModel.StatusMessage = UiText.Get("The execution request has changed. Review it again."); }
    }

    private async Task<StorageFolder?> PickFolderAsync()
    {
        var picker = new FolderPicker(); picker.FileTypeFilter.Add("*");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return await picker.PickSingleFolderAsync();
    }
    private async void Backup_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null) return;
        try { var folder = await PickFolderAsync(); if (folder is null) return; var target = Path.Combine(folder.Path, "OhMyStudy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); await _library.BackupAsync(target, _lifetime.Token); ViewModel.StatusMessage = UiText.Get("Backup saved."); }
        catch { ViewModel.StatusMessage = UiText.Get("Backup failed. Choose a folder with enough free space."); }
    }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        try { var folder = await PickFolderAsync(); if (folder is null) return; var destination = Path.Combine(Path.GetDirectoryName(_libraryDirectory)!, "Restored-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); await LibraryRepository.RestoreBackupAsync(folder.Path, destination, _lifetime.Token); if (!await SwitchLibraryAsync(destination)) return; ViewModel.StatusMessage = UiText.Get("Backup restored."); }
        catch { ViewModel.StatusMessage = _library is null ? UiText.Get("Restore failed. Choose a complete backup folder.") : UiText.Get("Restore failed. Current materials remain usable. Check the backup folder."); }
    }
    private async void MigrateLegacy_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await PickFolderAsync(); if (folder is null) return;
            var target = Path.Combine(Path.GetDirectoryName(_libraryDirectory)!, "LegacyBackup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            var destination = Path.Combine(Path.GetDirectoryName(_libraryDirectory)!, "Imported-" + DateTime.Now.ToString("yyyyMMdd-HHmmss"));
            await LibraryMigrationService.CopyAndUpgradeAsync(folder.Path, target, destination, _lifetime.Token);
            if (!await SwitchLibraryAsync(destination)) return;
            ViewModel.StatusMessage = UiText.Get("Legacy materials imported. The originals remain intact.");
        }
        catch { ViewModel.StatusMessage = _library is null ? UiText.Get("Import failed. Exit the legacy app and select its data folder.") : UiText.Get("Import failed. Current materials remain usable. Exit the legacy app and check its data folder."); }
    }

    private async Task<bool> SwitchLibraryAsync(string directory)
    {
        if (_modelRequestRunning) throw new InvalidOperationException(UiText.Get("Wait for the model request to finish."));
        if (_jobs is not null && (await _jobs.ListAsync()).Any(j => !j.State.IsTerminal())) throw new InvalidOperationException(UiText.Get("Finish the current task first."));
        if (!await ConfirmDiscardEditsAsync(false)) return false;
        string previousDirectory = _libraryDirectory;
        string? previousDocument = ViewModel.CurrentDocument?.Id;
        ++_documentGeneration; ++_questionReloadGeneration; ++_documentListGeneration; ++_taskRefreshGeneration;
        CancelPreviewLoad(); _reviewSourceRegion = null; _sourcePageId = null;
        try
        {
            await DisposeLibrarySessionAsync();
            ReleaseReadingResources(); _viewports.Clear(); _workspaceContexts.Clear();
            ViewModel.ClearLibrary();
            if (!await InitializeLibraryAsync(directory)) throw new IOException(UiText.Get("Could not open the library."));
            string pointer = _libraryPointerOverride ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMyStudy", "active-library.txt");
            string pointerDirectory = Path.GetDirectoryName(pointer)!;
            Directory.CreateDirectory(pointerDirectory);
            var temporary = Path.Combine(pointerDirectory, "active-library-" + Guid.NewGuid().ToString("N"));
            try
            {
                await File.WriteAllTextAsync(temporary, directory, new UTF8Encoding(false), _lifetime.Token);
                File.Move(temporary, pointer, true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            return true;
        }
        catch
        {
            try { await DisposeLibrarySessionAsync(); } catch { }
            if (!_lifetime.IsCancellationRequested && File.Exists(LegacyLibraryLocation.DatabaseFile(previousDirectory)) && await InitializeLibraryAsync(previousDirectory))
            {
                if (previousDocument is not null) try { await RefreshDocumentsAsync(previousDocument); } catch { }
                ViewModel.StatusMessage = UiText.Get("Switch failed. Original materials reopened.");
            }
            throw;
        }
    }

    private async Task<bool> ConfirmDiscardEditsAsync(bool exiting)
    {
        if (!ViewModel.HasUnsavedChanges && !HasUnsavedSettingsChanges) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = UiText.Get("Unsaved changes"),
            Content = exiting ? UiText.Get("Unsaved material or settings changes will be lost when you exit.") : UiText.Get("Unsaved material or settings changes will be lost when you switch libraries."),
            PrimaryButtonText = exiting ? UiText.Get("Exit") : UiText.Get("Switch"),
            CloseButtonText = UiText.Get("Back"),
            DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void EditQuestion_Click(object sender, RoutedEventArgs e) => await EditSelectedQuestionAsync();
}
