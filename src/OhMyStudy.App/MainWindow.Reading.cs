using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Services;
using OhMyStudy.Documents;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, (float Zoom, double X, double Y)> _pageViewports = new(StringComparer.Ordinal);
    private readonly NativePageImages _readingImages = new();
    private CancellationTokenSource? _prefetchCancellation;
    private CancellationTokenSource? _resolutionCancellation;
    private long _resolutionGeneration;
    private int _requestedRasterLevel;
    private bool _settingRaster;
    private int _currentRasterLevel;

    private void RememberPageViewport()
    {
        if (_sourcePageId is { } id && !_settingRaster)
            _pageViewports[id] = (SourceViewer.ZoomFactor, SourceViewer.HorizontalOffset, SourceViewer.VerticalOffset);
    }

    private CancellationToken BeginReadingLoad()
    {
        CancelPreviewLoad();
        CancelPrefetch();
        CancelResolutionUpgrade();
        _previewCancellation?.Dispose();
        _previewCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        return _previewCancellation.Token;
    }

    private void CancelPrefetch()
    {
        var old = _prefetchCancellation; _prefetchCancellation = null;
        if (old is not null) { old.Cancel(); old.Dispose(); }
    }

    private void CancelResolutionUpgrade()
    {
        ++_resolutionGeneration;
        var old = _resolutionCancellation; _resolutionCancellation = null;
        _requestedRasterLevel = 0;
        if (old is not null) { old.Cancel(); old.Dispose(); }
    }

    private int ReadingResolution()
    {
        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1;
        double pixels = Math.Max(_pageWidth, _pageHeight) * SourceViewer.ZoomFactor * scale;
        return pixels <= 1000 ? 1024 : pixels <= 1600 ? 1600 : pixels <= 2400 ? 2400 : 3600;
    }

    private async Task<RasterizedPage> GetReadingRasterAsync(LibraryRepository library, SourcePage page, int level, CancellationToken ct)
    {
        var key = new PageCacheKey(page.AssetId, level, 0);
        if (_pageCache.TryGet(key, out var existing)) return existing!;
        var asset = await library.ReadAssetAsync(page.AssetId, cancellationToken: ct);
        if (asset.Mime == "application/pdf") throw new IOException(UiText.Get("请重新打开或导入这份资料。"));
        var raster = await _readingImages.RasterForUiAsync(asset.Bytes, level, ct);
        ct.ThrowIfCancellationRequested();
        _pageCache.Set(key, raster);
        return raster;
    }

    private void StartReadingPrefetch(Document document, int current)
    {
        CancelPrefetch();
        if (_library is not { } library || _closed) return;
        _prefetchCancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        var ct = _prefetchCancellation.Token;
        long generation = _documentGeneration;
        _ = PrefetchAsync();
        async Task PrefetchAsync()
        {
            try
            {
                // Only the two immediate neighbours, one at a time, after foreground work.
                await Task.Delay(100, ct);
                foreach (int index in new[] { current + 1, current - 1 })
                {
                    if (_closed || generation != _documentGeneration || index < 0 || index >= document.Pages.Count) continue;
                    await GetReadingRasterAsync(library, document.Pages[index], 1024, ct);
                }
            }
            catch (OperationCanceledException) { }
            catch (ObjectDisposedException) { }
            catch (Exception) { /* Optional prefetch never replaces a foreground error or produces UI feedback. */ }
        }
    }

    private async void ImproveReadingResolutionAsync()
    {
        if (_settingRaster || _closed || ViewModel.IsSample || _library is not { } library || ViewModel.CurrentDocument is not { } document || _sourcePageId is null) return;
        var page = document.Pages.FirstOrDefault(p => p.Id == _sourcePageId);
        if (page is null) return;
        int level = ReadingResolution();
        if (level <= _currentRasterLevel || _requestedRasterLevel >= level) return;
        CancelPrefetch();
        CancelResolutionUpgrade();
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_previewCancellation?.Token ?? _lifetime.Token);
        _resolutionCancellation = cancellation;
        _requestedRasterLevel = level;
        long resolution = _resolutionGeneration;
        long generation = _previewGeneration;
        var ct = cancellation.Token;
        try
        {
            await Task.Delay(100, ct);
            if (generation != _previewGeneration || resolution != _resolutionGeneration || page.Id != _sourcePageId) return;
            var raster = await GetReadingRasterAsync(library, page, level, ct);
            if (generation != _previewGeneration || resolution != _resolutionGeneration || page.Id != _sourcePageId || _closed || level < _currentRasterLevel) return;
            var bitmap = await CreateRasterImageAsync(raster);
            if (generation != _previewGeneration || resolution != _resolutionGeneration || page.Id != _sourcePageId || _closed || level < _currentRasterLevel) return;
            ImportedImage.Source = bitmap; _currentRasterLevel = level;
            // Paper geometry and source coordinates remain based on the original, never the mip level.
            StartReadingPrefetch(document, _sourcePageIndex);
        }
        catch (OperationCanceledException) { }
        catch (ObjectDisposedException) { }
        catch { ViewModel.StatusMessage = UiText.Get("页面放大失败，可缩小后重试。"); }
        finally
        {
            if (ReferenceEquals(_resolutionCancellation, cancellation)) { _resolutionCancellation = null; _requestedRasterLevel = 0; }
            cancellation.Dispose();
        }
    }

    private void ReleaseReadingResources()
    {
        CancelPrefetch(); CancelResolutionUpgrade(); _pageCache.Clear(); _pageViewports.Clear();
        _currentRasterLevel = 0; _sourcePageId = null;
        ImportedImage.Source = null;
    }
}
