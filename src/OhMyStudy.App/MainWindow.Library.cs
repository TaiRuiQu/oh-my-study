using System.Collections.ObjectModel;
using System.Security.Cryptography;
using System.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.App.Services;
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
    private bool _loadingLibrary;
    private string _libraryDirectory = "";
    private string? _libraryPointerOverride;
    private string? _activeJob;
    private long _documentGeneration;
    private int _sourcePageIndex;
    private string? _sourcePageId;
    private bool _restoringRotation;
    private bool _programmaticSelection;
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
        if (_questionEditorOpen) { ViewModel.StatusMessage = "请先保存或关闭编辑窗口。"; return; }
        _closeReviewOpen = true;
        try { if (!await ConfirmDiscardEditsAsync(true)) return; }
        catch { ViewModel.StatusMessage = "请先关闭当前窗口，再退出应用。"; return; }
        finally { _closeReviewOpen = false; }
        _shutdownStarted = true;
        _lifetime.Cancel();
        ViewModel.StatusMessage = "正在结束任务…";
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
        catch { if (!_closed && generation == _documentGeneration && _sourcePageId == sourceId) { _rotation = oldRotation; Rotate(0); ViewModel.StatusMessage = "方向未能保存，请等待任务结束后重试。"; } }
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
            _libraryDirectory = directory ?? (selected is not null && Path.IsPathFullyQualified(selected) && File.Exists(LegacyLibraryLocation.DatabaseFile(selected)) ? selected : Path.Combine(appData, "Library"));
            var mode = File.Exists(LegacyLibraryLocation.DatabaseFile(_libraryDirectory)) ? LibraryOpenMode.ReadWrite : LibraryOpenMode.CreateNew;
            _library = await LibraryRepository.OpenAsync(new(_libraryDirectory, mode) { MediaProcessor = new NativeMediaProcessor() }, _lifetime.Token);
            string privateRuntime = Path.Combine(AppContext.BaseDirectory, "runtime", "python");
            var activeLibrary = _library;
            _jobs = await JobQueue.CreateAsync(new LibraryJobStore(_library), new GradingRunner(_library, new NativePageImages()),
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
                    try { await _jobs.CancelAsync(id); await RefreshTasksAsync(); } catch { ViewModel.StatusMessage = "取消未完成，请稍后重试。"; }
            };
            DocumentPicker.ItemsSource = _documents;
            ProviderPicker.ItemsSource = _providers;
            _settings = await _library.GetSettingsAsync(_lifetime.Token);
            ExecutionModePicker.SelectedIndex = _settings.ExecutionMode switch { "sandbox" => 1, "bypass_permission" => 2, _ => 0 };
            await RefreshProvidersAsync();
            await RefreshDocumentsAsync();
            _ = ObserveJobsAsync();
            await RefreshTasksAsync();
            if (_documents.Count == 0)
            {
                ViewModel.ClearLibrary(); OriginalCaption.Text = "导入资料后在这里查看。";
                WorkspaceNotice.IsOpen = false;
            }
            string legacy = LegacyLibraryLocation.ResolveDefault(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData));
            if (directory is null && File.Exists(LegacyLibraryLocation.DatabaseFile(legacy)) && _documents.Count == 0)
            {
                WorkspaceNotice.Title = "发现旧版资料";
                WorkspaceNotice.Message = "可从更多菜单导入，原资料会保留。"; WorkspaceNotice.IsOpen = true;
            }
            complete = true;
            return true;
        }
        catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { return false; }
        catch { ViewModel.StatusMessage = "资料库无法打开，请关闭其他窗口后重新打开。"; GradeButton.IsEnabled = false; return false; }
        finally
        {
            if (!complete)
            {
                try { await DisposeLibrarySessionAsync(); } catch { }
                ViewModel.SaveQuestionAsync = null; ViewModel.CancelCurrentJob = null;
                _programmaticSelection = true;
                try { _documents.Clear(); _providers.Clear(); }
                finally { _programmaticSelection = false; }
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
        var documents = await _library.ListDocumentsAsync(_lifetime.Token);
        if (_closed || generation != _documentListGeneration) return;
        _programmaticSelection = true;
        _documents.Clear(); foreach (var document in documents) _documents.Add(document);
        DocumentPicker.SelectedItem = _documents.FirstOrDefault(d => d.Id == (selectId ?? ViewModel.CurrentDocument?.Id)) ?? _documents.FirstOrDefault();
        _programmaticSelection = false;
        if (DocumentPicker.SelectedItem is Document selected) await LoadDocumentAsync(selected);
    }

    private async void DocumentPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_programmaticSelection || _closed) return;
        if (DocumentPicker.SelectedItem is Document document)
            try { await LoadDocumentAsync(document); } catch { ViewModel.StatusMessage = "资料无法打开，请重新选择。"; }
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
        _programmaticSelection = true;
        try { DocumentPicker.SelectedItem = _documents.FirstOrDefault(item => item.Id == current.Id); }
        finally { _programmaticSelection = false; }
        GradeButton.IsEnabled = current.Pages.Count > 0 && !current.PendingPdfPages;
        WorkspaceNotice.IsOpen = current.PendingPdfPages;
        WorkspaceNotice.Title = "资料尚未准备完成";
        WorkspaceNotice.Message = "请重新打开或导入文件后重试。";
        var view = _viewports.GetValueOrDefault(current.Id);
        _sourcePageIndex = Math.Clamp(view.Page, 0, Math.Max(0, current.Pages.Count - 1));
        await ShowSourcePageAsync(_sourcePageIndex);
        if (_closed || generation != _documentGeneration || ViewModel.CurrentDocument?.Id != current.Id) return;
        if (view.Zoom > 0) SourceViewer.ChangeView(view.X, view.Y, view.Zoom, true);
        await RestoreWorkspaceContextAsync("materials:" + current.Id, generation);
        UpdateSourceHighlight();
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
            ViewModel.LoadLibrary(questions, ViewModel.SectionTitle == "错题");
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
                OriginalCaption.Text = page.Role == "reference" ? "参考答案" : "原件";
                PdfPageControls.Visibility = document.Pages.Count > 1 ? Visibility.Visible : Visibility.Collapsed;
                PdfPageLabel.Text = $"第 {index + 1} / {document.Pages.Count} 页";
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
        catch { if (!_closed && generation == _previewGeneration) ViewModel.StatusMessage = "页面无法打开，请重新选择或导入。"; }
    }

    private async Task ImportFilesAsync(IReadOnlyList<StorageFile> files, bool reference = false, string? referenceDocumentId = null)
    {
        if (_library is null || files.Count == 0) return;
        if (!await _importGate.WaitAsync(0)) return;
        try
        {
            if (files.Count > LibraryLimits.MaximumPages) { ViewModel.StatusMessage = "一次最多导入12页。"; return; }
            ImportMode mode = ImportMode.AiGrading;
            if (!reference)
            {
                var choice = new ComboBox { MinWidth = 320, SelectedIndex = 1 };
                choice.Items.Add(new ComboBoxItem { Content = "已有教师批改" }); choice.Items.Add(new ComboBoxItem { Content = "待AI批改" }); choice.Items.Add(new ComboBoxItem { Content = "空白试卷" });
                var dialog = new ContentDialog
                {
                    XamlRoot = RootGrid.XamlRoot,
                    Title = "导入资料",
                    Content = choice,
                    PrimaryButtonText = "导入",
                    CloseButtonText = "取消",
                    DefaultButton = ContentDialogButton.Primary
                };
                if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
                mode = choice.SelectedIndex switch { 0 => ImportMode.TeacherGraded, 2 => ImportMode.Blank, _ => ImportMode.AiGrading };
            }
            ViewModel.StatusMessage = "正在导入…";
            var sources = new List<ImportSource>();
            long total = 0;
            foreach (var file in files)
            {
                var properties = await file.GetBasicPropertiesAsync();
                if (properties.Size > LibraryLimits.MaximumAssetBytes || total + (long)properties.Size > LibraryLimits.MaximumBatchBytes)
                    throw new InvalidDataException("文件过大。");
                byte[] bytes = await File.ReadAllBytesAsync(file.Path, _lifetime.Token);
                total += bytes.Length; sources.Add(new(file.Name, bytes, reference ? "reference" : "material"));
            }
            var document = reference && referenceDocumentId is not null
                ? await _library.AppendDocumentSourcesAsync(referenceDocumentId, sources, _lifetime.Token)
                : await _library.ImportDocumentAsync(Path.GetFileNameWithoutExtension(files[0].Name), mode, sources, _lifetime.Token);
            await RefreshDocumentsAsync(document.Id);
            ViewModel.StatusMessage = reference ? "参考答案已添加，可选择重新整理。" : "已导入。";
        }
        catch (OperationCanceledException) { ViewModel.StatusMessage = "已取消导入。"; }
        catch { ViewModel.StatusMessage = "导入失败，请检查文件格式、页数和大小后重试。"; }
        finally { _importGate.Release(); }
    }

    private async Task<IReadOnlyList<StorageFile>> PickFilesAsync()
    {
        var picker = new FileOpenPicker { SuggestedStartLocation = PickerLocationId.DocumentsLibrary };
        foreach (string extension in new[] { ".png", ".jpg", ".jpeg", ".webp", ".pdf" }) picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        return await picker.PickMultipleFilesAsync();
    }
    private async void AddReferences_Click(object sender, RoutedEventArgs e)
    {
        if (ViewModel.CurrentDocument is not { } document) { ViewModel.StatusMessage = "请先选择资料。"; return; }
        try { await ImportFilesAsync(await PickFilesAsync(), true, document.Id); } catch { ViewModel.StatusMessage = "无法打开文件，请重试。"; }
    }

    private async Task RefreshProvidersAsync()
    {
        if (_library is null) return;
        var providers = await _library.ListProvidersAsync(_lifetime.Token);
        _providers.Clear(); foreach (var provider in providers) _providers.Add(provider);
        ProviderPicker.SelectedItem = _providers.FirstOrDefault(p => p.Id == _selectedProviderId) ?? _providers.FirstOrDefault();
    }
    private void ProviderPicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ProviderPicker.SelectedItem is not Provider provider) return;
        _selectedProviderId = provider.Id; ProviderName.Text = provider.Name; ProviderUrl.Text = provider.BaseUrl;
        ProtocolPicker.SelectedIndex = (int)provider.Protocol; ProviderKey.Password = "";
        ModelId.Text = provider.Models.FirstOrDefault()?.Id ?? ""; ProviderFeedback.Text = "";
    }
    private void NewProvider_Click(object sender, RoutedEventArgs e)
    {
        ProviderPicker.SelectedIndex = -1; _selectedProviderId = null; ProviderName.Text = ""; ProviderUrl.Text = ""; ProviderKey.Password = ""; ModelId.Text = ""; ProviderFeedback.Text = "";
    }
    private async void SaveProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null) return;
        byte[]? key = null;
        try
        {
            var old = _providers.FirstOrDefault(p => p.Id == _selectedProviderId);
            var protocol = (Protocol)Math.Clamp(ProtocolPicker.SelectedIndex, 0, 2);
            using var validation = new ProviderClient(protocol, ProviderUrl.Text.Trim());
            var models = old?.Models.ToList() ?? [];
            if (!string.IsNullOrWhiteSpace(ModelId.Text) && models.All(m => m.Id != ModelId.Text.Trim()))
                models.Add(new() { Id = ModelId.Text.Trim(), Capabilities = Capabilities.Unknown, Currency = "USD" });
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
            if (models.Count > 0) _settings = await _library.SaveSettingsAsync(_settings with { DefaultModel = saved.Provider.Id + ":" + models[^1].Id }, _lifetime.Token);
            await RefreshProvidersAsync(); ProviderFeedback.Text = "已保存。";
        }
        catch { ProviderFeedback.Text = "无法保存，请检查名称、地址和密钥。"; }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }
    private async void DeleteProvider_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null || _selectedProviderId is null) return;
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "删除供应商？", Content = "已有资料与批改记录会保留。", PrimaryButtonText = "删除", CloseButtonText = "取消" };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        try { await new ProviderCredentialService(_library, new WindowsCredentialVault()).DeleteAsync(_selectedProviderId, _lifetime.Token); NewProvider_Click(sender, e); await RefreshProvidersAsync(); }
        catch { ProviderFeedback.Text = "无法删除，请稍后重试。"; }
    }
    private async void RefreshModels_Click(object sender, RoutedEventArgs e)
    {
        if (_library is null || ProviderPicker.SelectedItem is not Provider provider) return;
        byte[]? key = null;
        try
        {
            key = WindowsCredentialStore.Read(provider.KeyRef ?? "");
            if (key is null) { ProviderFeedback.Text = "请先保存密钥。"; return; }
            using var client = new ProviderClient(provider.Protocol, provider.BaseUrl);
            var page = await client.ListModelsAsync(new(Encoding.UTF8.GetString(key)), cancellationToken: _lifetime.Token);
            var models = provider.Models.ToDictionary(m => m.Id, StringComparer.Ordinal);
            foreach (var model in page.Models) if (!models.ContainsKey(model.Id)) models[model.Id] = new() { Id = model.Id, Capabilities = model.Capabilities, Currency = "USD" };
            await _library.UpsertProviderAsync(provider with { Models = models.Values.ToList() }, _lifetime.Token);
            await RefreshProvidersAsync(); ProviderFeedback.Text = $"已有 {models.Count} 个模型。";
        }
        catch { ProviderFeedback.Text = "获取失败，请检查地址、密钥和网络。"; }
        finally { if (key is not null) CryptographicOperations.ZeroMemory(key); }
    }

    private async void ExecutionModePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || ExecutionDescription is null) return;
        var mode = ExecutionModePicker.SelectedIndex switch { 1 => "sandbox", 2 => "bypass_permission", _ => "request_permission" };
        ExecutionDescription.Text = mode == "sandbox" ? "沙箱未就绪时会在开始前说明。" : mode == "request_permission" ? "执行代码或命令前，由你决定是否允许。" : "代码与命令可访问当前账户的文件和网络。";
        if (_loadingLibrary || _library is null) return;
        try { _settings = await _library.SaveSettingsAsync(_settings with { ExecutionMode = mode }, _lifetime.Token); } catch { ViewModel.StatusMessage = "设置未能保存。"; }
    }
    private async void Grade_Click(object sender, RoutedEventArgs e) => await StartGradingAsync(false);
    private async void ReadReferences_Click(object sender, RoutedEventArgs e) => await StartGradingAsync(true);

    private async Task StartGradingAsync(bool references)
    {
        if (_library is null || _jobs is null || ViewModel.CurrentDocument is not { } document) return;
        try
        {
            var providers = await _library.ListProvidersAsync(_lifetime.Token);
            var provider = providers.FirstOrDefault(p => p.Models.Any(m => p.Id + ":" + m.Id == _settings.DefaultModel)) ?? providers.FirstOrDefault(p => p.Models.Any(m => m.Id == _settings.DefaultModel)) ?? providers.FirstOrDefault();
            if (provider is null || provider.Models.Count == 0 || provider.KeyRef is null)
            { ViewModel.StatusMessage = "请先在设置中添加模型和密钥。"; return; }
            var model = provider.Models.FirstOrDefault(m => provider.Id + ":" + m.Id == _settings.DefaultModel || m.Id == _settings.DefaultModel) ?? provider.Models[0];
            var availableModels = providers.SelectMany(p => p.Models.Select(m => (Provider: p, Model: m))).ToList();
            var choice = new ComboBox { MinWidth = 320 };
            foreach (var available in availableModels) choice.Items.Add(available.Provider.Name + " / " + available.Model.Id);
            choice.SelectedIndex = availableModels.FindIndex(p => p.Provider.Id == provider.Id && p.Model.Id == model.Id);
            var thinkingChoice = new ComboBox { Header = "思考", MinWidth = 320, SelectedIndex = 0 };
            thinkingChoice.Items.Add("默认"); foreach (var option in model.Capabilities.Thinking) thinkingChoice.Items.Add(ThinkingLabel(option));
            var unknown = new CheckBox { Content = "允许尝试尚未确认能力的模型" };
            choice.SelectionChanged += (_, _) => { thinkingChoice.Items.Clear(); thinkingChoice.Items.Add("默认"); if (choice.SelectedIndex >= 0) foreach (var option in availableModels[choice.SelectedIndex].Model.Capabilities.Thinking) thinkingChoice.Items.Add(ThinkingLabel(option)); thinkingChoice.SelectedIndex = 0; };
            var preferred = _settings.ExecutionMode switch { "sandbox" => ExecutionMode.Sandbox, "bypass_permission" => ExecutionMode.BypassPermission, _ => ExecutionMode.RequestPermission };
            var capability = await SandboxCapability.ProbeAsync(Path.Combine(AppContext.BaseDirectory, "runtime", "python"), _lifetime.Token);
            var policy = ExecutionPolicy.Freeze(preferred, capability, _settings.SandboxNetworking);
            var content = new StackPanel { Spacing = 14 }; content.Children.Add(choice); content.Children.Add(thinkingChoice); content.Children.Add(unknown);
            if (policy.Warning is not null) content.Children.Add(new TextBlock { Text = "沙箱暂不可用。本次代码与命令将在本机运行，可访问当前账户的文件和网络。", TextWrapping = TextWrapping.Wrap });
            content.Children.Add(new TextBlock { Text = "资料会发送给所选模型，可能产生费用。", TextWrapping = TextWrapping.Wrap });
            var dialog = new ContentDialog
            {
                XamlRoot = RootGrid.XamlRoot,
                Title = references ? "整理参考答案" : "开始整理",
                Content = content,
                PrimaryButtonText = "开始",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Primary
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
            (provider, model) = availableModels[choice.SelectedIndex];
            Thinking? thinking = thinkingChoice.SelectedIndex > 0 ? model.Capabilities.Thinking[thinkingChoice.SelectedIndex - 1] : null;
            var input = new GradingInput { Document = document, Provider = provider, Model = model, Thinking = thinking, AllowUnknown = unknown.IsChecked == true };
            _activeJob = (await _jobs.SubmitAsync(references ? DiagnosticKind.References : DiagnosticKind.Grading, policy, input, _lifetime.Token)).Id;
            await RefreshTasksAsync(); ViewModel.StatusMessage = "已加入队列。";
        }
        catch { ViewModel.StatusMessage = "任务无法开始，请检查模型设置或等待当前任务结束。"; }
    }
    private static string ThinkingLabel(Thinking value) => value switch
    { ResponsesThinking v => v.Effort, AdaptiveThinking v => v.Effort, BudgetThinking v => $"{v.Tokens} tokens", GeminiLevelThinking v => v.Level, GeminiBudgetThinking v => v.Tokens == -1 ? "自动" : $"{v.Tokens} tokens", _ => "默认" };

    private async Task ObserveJobsAsync()
    {
        if (_jobs is not { } observedJobs) return;
        try
        {
            await foreach (var change in observedJobs.WatchAsync(_lifetime.Token))
                DispatcherQueue.TryEnqueue(async () =>
                {
                    if (_closed || _lifetime.IsCancellationRequested || !ReferenceEquals(_jobs, observedJobs)) return;
                    try { await RefreshTasksAsync(); if (change.Kind is "completed" or "failed" or "cancelled") await ReloadQuestionsAsync(); }
                    catch (OperationCanceledException) when (_lifetime.IsCancellationRequested) { }
                    catch { if (!_closed) ViewModel.StatusMessage = "任务状态暂时无法更新，请重新打开任务页。"; }
                });
        }
        catch (OperationCanceledException) { }
        catch { if (!_closed && ReferenceEquals(_jobs, observedJobs)) DispatcherQueue.TryEnqueue(() => ViewModel.StatusMessage = "任务状态暂时无法更新。"); }
    }
    private async Task RefreshTasksAsync()
    {
        if (_jobs is null || _closed) return;
        long generation = ++_taskRefreshGeneration;
        var jobs = await _jobs.ListAsync(_lifetime.Token);
        if (_closed || generation != _taskRefreshGeneration) return;
        var active = jobs.FirstOrDefault(j => !j.State.IsTerminal());
        _activeJob = active?.Id; ViewModel.UpdateJob(active);
        var cards = new List<UIElement>();
        if (jobs.Count == 0) cards.Add(new TextBlock { Text = "暂无任务。", Margin = new Thickness(0, 12, 0, 0) });
        foreach (var job in jobs)
        {
            var panel = new StackPanel { Spacing = 10 };
            panel.Children.Add(new TextBlock { Text = job.Input?.Document.Name ?? "资料整理", FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
            panel.Children.Add(new TextBlock { Text = job.Message, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = job.ExecutionPolicy.ExecutorKind == ExecutorKind.WindowsSandbox ? "沙箱运行" : "本机运行", Opacity = .65 });
            if (job.Input is not null && _library is not null)
            {
                var details = await _library.ListJobModelCallsAsync(job.Id, _lifetime.Token);
                if (_closed || generation != _taskRefreshGeneration) return;
                var costs = details.Where(c => c.TryGetProperty("estimated_cost", out var cost) && cost.ValueKind == System.Text.Json.JsonValueKind.Number)
                    .Select(c => c.GetProperty("estimated_cost").GetDouble()).ToArray();
                ulong inputTokens = 0, outputTokens = 0;
                foreach (var call in details)
                    if (call.TryGetProperty("usage", out var usage))
                    { if (usage.TryGetProperty("input", out var input) && input.TryGetUInt64(out var a)) inputTokens += a; if (usage.TryGetProperty("output", out var output) && output.TryGetUInt64(out var b)) outputTokens += b; }
                if (details.Count > 0) panel.Children.Add(new TextBlock { Text = $"用量：输入 {inputTokens} · 输出 {outputTokens}", Opacity = .65 });
                if (details.Count > 0) panel.Children.Add(new TextBlock { Text = costs.Length == details.Count ? $"估算费用：{costs.Sum():0.######} {job.Input.Model.Currency}" : "费用暂无法估算", Opacity = .65 });
            }
            if (!job.State.IsTerminal())
            {
                var cancel = new Button { Content = "取消" };
                cancel.Click += async (_, _) => { try { await _jobs.CancelAsync(job.Id); await RefreshTasksAsync(); } catch { ViewModel.StatusMessage = "无法取消，请重试。"; } }; panel.Children.Add(cancel);
            }
            if (job.PendingPermission is { } review)
            {
                var button = new Button { Content = "查看执行请求", Style = (Style)Microsoft.UI.Xaml.Application.Current.Resources["AccentButtonStyle"] };
                button.Click += async (_, _) => await ReviewPermissionAsync(job.Id, review); panel.Children.Add(button);
            }
            cards.Add(new Border
            {
                Child = panel,
                Padding = new Thickness(20),
                CornerRadius = new CornerRadius(10),
                Background = (Brush)Microsoft.UI.Xaml.Application.Current.Resources["CardBackgroundFillColorDefaultBrush"]
            });
        }
        if (_closed || generation != _taskRefreshGeneration) return;
        TaskCards.Children.Clear(); foreach (var card in cards) TaskCards.Children.Add(card);
    }
    private async Task ReviewPermissionAsync(string id, PermissionReview review)
    {
        if (_jobs is null) return;
        var text = new TextBox { Text = review.Call.Content, IsReadOnly = true, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MaxHeight = 340 };
        var task = new CheckBox { Content = "本任务的后续调用也允许" };
        var body = new StackPanel { Spacing = 12 }; body.Children.Add(new TextBlock { Text = "将在本机运行，可访问当前账户的文件和网络。", TextWrapping = TextWrapping.Wrap });
        body.Children.Add(new TextBlock { Text = $"{(review.Call.Tool == ToolKind.Python ? "Python" : "PowerShell")} · 最长 {review.Call.TimeoutMs / 1000d:0.###} 秒\n目录：{review.Call.WorkingDirectory}\n参数：{DomainJson.Serialize(review.Call.Arguments)}", TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true });
        body.Children.Add(text); body.Children.Add(task);
        var dialog = new ContentDialog { XamlRoot = RootGrid.XamlRoot, Title = "允许执行？", Content = body, PrimaryButtonText = "允许", SecondaryButtonText = "拒绝", CloseButtonText = "稍后" };
        var result = await dialog.ShowAsync(); if (result == ContentDialogResult.None) return;
        try { await _jobs.DecideAsync(id, review.Digest, result == ContentDialogResult.Secondary ? PermissionChoice.Deny : task.IsChecked == true ? PermissionChoice.AllowTask : PermissionChoice.AllowOnce); await RefreshTasksAsync(); }
        catch { ViewModel.StatusMessage = "执行请求已变化，请重新查看。"; }
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
        try { var folder = await PickFolderAsync(); if (folder is null) return; var target = Path.Combine(folder.Path, "OhMyStudy-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); await _library.BackupAsync(target, _lifetime.Token); ViewModel.StatusMessage = "备份已保存。"; }
        catch { ViewModel.StatusMessage = "备份失败，请选择有足够空间的文件夹。"; }
    }
    private async void RestoreBackup_Click(object sender, RoutedEventArgs e)
    {
        try { var folder = await PickFolderAsync(); if (folder is null) return; var destination = Path.Combine(Path.GetDirectoryName(_libraryDirectory)!, "Restored-" + DateTime.Now.ToString("yyyyMMdd-HHmmss")); await LibraryRepository.RestoreBackupAsync(folder.Path, destination, _lifetime.Token); if (!await SwitchLibraryAsync(destination)) return; ViewModel.StatusMessage = "已恢复备份。"; }
        catch { ViewModel.StatusMessage = _library is null ? "恢复失败，请选择完整的备份文件夹。" : "恢复失败，当前资料仍可使用。请检查备份文件夹。"; }
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
            ViewModel.StatusMessage = "已导入旧版资料，原资料保持完整。";
        }
        catch { ViewModel.StatusMessage = _library is null ? "无法导入，请先退出旧版应用并选择资料文件夹。" : "无法导入，当前资料仍可使用。请先退出旧版应用并检查资料文件夹。"; }
    }

    private async Task<bool> SwitchLibraryAsync(string directory)
    {
        if (_jobs is not null && (await _jobs.ListAsync()).Any(j => !j.State.IsTerminal())) throw new InvalidOperationException("请先结束当前任务。");
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
            if (!await InitializeLibraryAsync(directory)) throw new IOException("资料库无法打开。");
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
            if (!_lifetime.IsCancellationRequested && await InitializeLibraryAsync(previousDirectory))
            {
                if (previousDocument is not null) try { await RefreshDocumentsAsync(previousDocument); } catch { }
                ViewModel.StatusMessage = "切换失败，已重新打开原资料。";
            }
            throw;
        }
    }

    private async Task<bool> ConfirmDiscardEditsAsync(bool exiting)
    {
        if (!ViewModel.HasUnsavedChanges) return true;
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot, Title = "有未保存的修改",
            Content = exiting ? "退出后，未保存的修改会丢失。" : "切换资料库后，未保存的修改会丢失。",
            PrimaryButtonText = exiting ? "退出" : "切换", CloseButtonText = "返回", DefaultButton = ContentDialogButton.Close
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    private async void EditQuestion_Click(object sender, RoutedEventArgs e) => await EditSelectedQuestionAsync();
}
