using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using OhMyStudy.Domain;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task RunPerformanceSmokeAsync(string reportPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(reportPath)!);
        string root = Path.Combine(Path.GetDirectoryName(reportPath)!, "performance-library-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        _libraryPointerOverride = Path.Combine(root, "active-library.txt");
        var clock = Stopwatch.StartNew();
        var memory = new ConcurrentQueue<PerformanceMemorySample>();
        var dispatch = new ConcurrentQueue<PerformanceDispatcherSample>();
        var frames = new List<PerformanceRenderingSample>();
        var pages = new List<PerformancePageSample>();
        var checks = new Dictionary<string, bool>(StringComparer.Ordinal);
        string phase = "initialize";
        string? error = null;
        long lastRendering = 0;
        string lastRenderingPhase = "";
        int exitCode = 1;
        using var sampling = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        Task? memoryTask = null, dispatchTask = null;
        void Rendering(object? sender, object args)
        {
            long now = Stopwatch.GetTimestamp();
            string current = Volatile.Read(ref phase);
            if (current == lastRenderingPhase && lastRendering != 0)
                frames.Add(new(clock.Elapsed.TotalMilliseconds, current, Stopwatch.GetElapsedTime(lastRendering, now).TotalMilliseconds));
            lastRendering = now; lastRenderingPhase = current;
        }
        void MemoryPoint()
        {
            using var process = Process.GetCurrentProcess(); process.Refresh();
            memory.Enqueue(new(clock.Elapsed.TotalMilliseconds, Volatile.Read(ref phase), process.PrivateMemorySize64, process.WorkingSet64,
                _pageCache.RetainedBytes, process.TotalProcessorTime.TotalMilliseconds, GC.GetTotalAllocatedBytes(false), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2)));
        }
        async Task SampleMemoryAsync()
        {
            try { while (!sampling.IsCancellationRequested) { MemoryPoint(); await Task.Delay(200, sampling.Token); } }
            catch (OperationCanceledException) { }
        }
        async Task SampleDispatcherAsync()
        {
            try
            {
                while (!sampling.IsCancellationRequested)
                {
                    long sent = Stopwatch.GetTimestamp();
                    string current = Volatile.Read(ref phase);
                    var reply = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
                    bool queued = DispatcherQueue.TryEnqueue(() => reply.TrySetResult(Stopwatch.GetElapsedTime(sent).TotalMilliseconds));
                    if (!queued) { dispatch.Enqueue(new(clock.Elapsed.TotalMilliseconds, current, null, "queue-unavailable")); break; }
                    try { dispatch.Enqueue(new(clock.Elapsed.TotalMilliseconds, current, await reply.Task.WaitAsync(TimeSpan.FromSeconds(3), sampling.Token), null)); }
                    catch (TimeoutException) { dispatch.Enqueue(new(clock.Elapsed.TotalMilliseconds, current, null, "timeout-after-3000ms")); }
                    await Task.Delay(25, sampling.Token);
                }
            }
            catch (OperationCanceledException) { }
        }
        try
        {
            if (!await InitializeLibraryAsync(Path.Combine(root, "library")) || _library is null) throw new IOException("Synthetic performance library could not initialize");
            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!DispatcherQueue.TryEnqueue(() => ready.TrySetResult())) throw new IOException("UI dispatcher unavailable");
            await ready.Task;
            checks["empty-library-ready"] = RootGrid.XamlRoot is not null && ViewModel.Questions.Count == 0 && !ViewModel.IsSample;
            var marker = new PerformanceReady(Environment.ProcessId, DateTimeOffset.UtcNow, checks["empty-library-ready"], "initialized-empty-library-and-dispatcher-response");
            await File.WriteAllTextAsync(reportPath + ".ready", JsonSerializer.Serialize(marker, PerformanceJson.Default.PerformanceReady));
            CompositionTarget.Rendering += Rendering;
            memoryTask = Task.Run(SampleMemoryAsync); dispatchTask = Task.Run(SampleDispatcherAsync);
            Volatile.Write(ref phase, "empty-natural-idle");
            await Task.Delay(1800);
            Volatile.Write(ref phase, "generate-fixtures");
            var sourceImages = new[] { await MakePerformancePngAsync(900, 1200), await MakePerformancePngAsync(1800, 2400), await MakePerformancePngAsync(2400, 3200) };
            var sources = Enumerable.Range(0, 12).Select(index => new OhMyStudy.Infrastructure.Storage.ImportSource("page-" + index + ".png", sourceImages[index % sourceImages.Length])).ToArray();
            Volatile.Write(ref phase, "import");
            var document = await _library.ImportDocumentAsync("阅读性能示例", ImportMode.Blank, sources);
            await RefreshDocumentsAsync(document.Id);
            checks["twelve-mixed-resolution-pages"] = document.Pages.Count == 12 && document.Pages.Select(page => page.Width).Distinct().Count() == 3;
            for (int cycle = 0; cycle < 3; cycle++)
            {
                string readingPhase = cycle == 0 ? "first-reading-pass" : "return-reading-pass-" + cycle;
                Volatile.Write(ref phase, readingPhase);
                for (int index = 0; index < document.Pages.Count; index++)
                {
                    long before = _pageCache.RetainedBytes;
                    var watch = Stopwatch.StartNew(); await ShowSourcePageAsync(index);
                    pages.Add(new(clock.Elapsed.TotalMilliseconds, readingPhase, cycle, index, document.Pages[index].Width ?? 0,
                        document.Pages[index].Height ?? 0, watch.Elapsed.TotalMilliseconds, before, _pageCache.RetainedBytes, _sourcePageId == document.Pages[index].Id));
                    await Task.Delay(20);
                }
                MemoryPoint(); await Task.Delay(250);
            }
            checks["all-page-requests-displayed"] = pages.Count == 36 && pages.All(page => page.Displayed);
            Volatile.Write(ref phase, "continuous-scroll-and-zoom");
            ViewModel.LoadSample(); QuestionList.UpdateLayout();
            int maximumRealized = 0;
            for (int step = 0; step < 240; step++)
            {
                var listScroller = FindVisualChild<ScrollViewer>(QuestionList);
                if (listScroller is not null)
                {
                    double progress = step % 120 / 119d;
                    if (step >= 120) progress = 1 - progress;
                    listScroller.ChangeView(null, listScroller.ScrollableHeight * progress, null, true);
                }
                SourceViewer.ChangeView(null, null, .65f + (float)(.55 * (1 + Math.Sin(step / 12d))), true);
                maximumRealized = Math.Max(maximumRealized, QuestionList.ItemsPanelRoot is Panel panel ? panel.Children.Count : 0);
                await Task.Delay(16);
            }
            checks["five-hundred-questions-virtualized"] = ViewModel.Questions.Count == 500 && maximumRealized is > 0 and < 500;
            checks["rendering-callbacks-observed"] = frames.Count(sample => sample.Phase == "continuous-scroll-and-zoom") >= 10;
            Volatile.Write(ref phase, "post-workload-natural-idle"); await Task.Delay(1500);
            Volatile.Write(ref phase, "release-reading-and-questions");
            ReleaseReadingResources(); _pageViewports.Clear(); _viewports.Clear(); _workspaceContexts.Clear();
            ViewModel.ClearLibrary(); QuestionContentHost.Content = null; MemoryPoint();
            Volatile.Write(ref phase, "released-natural-idle"); await Task.Delay(2500); MemoryPoint();
            checks["cache-byte-budget"] = memory.All(sample => sample.RasterCacheBytes <= 64L * 1024 * 1024);
            checks["explicit-release-clears-cache"] = _pageCache.RetainedBytes == 0;
            checks["native-aot"] = !RuntimeFeature.IsDynamicCodeSupported;
            checks["dispatcher-samples-completed"] = dispatch.Count >= 10 && dispatch.All(sample => sample.Error is null);
            exitCode = checks.Values.All(value => value) ? 0 : 1;
        }
        catch (Exception exception) { error = exception.ToString(); }
        finally
        {
            sampling.Cancel(); CompositionTarget.Rendering -= Rendering;
            if (memoryTask is not null) await memoryTask;
            if (dispatchTask is not null) await dispatchTask;
            var frameWork = frames.Where(sample => sample.Phase == "continuous-scroll-and-zoom").ToArray();
            const double referenceInterval = 1000d / 60;
            long estimatedMisses = frameWork.Sum(sample => Math.Max(0L, (long)Math.Floor(sample.IntervalMilliseconds / referenceInterval) - 1));
            var report = new NativePerformanceReport(DateTimeOffset.UtcNow, exitCode == 0, Environment.ProcessId, root, Environment.OSVersion.VersionString,
                Environment.ProcessorCount, RootGrid.XamlRoot?.RasterizationScale ?? 0, clock.Elapsed.TotalMilliseconds, checks, error,
                PerformanceDistribution.Of(pages.Select(page => page.LoadMilliseconds)), PerformanceDistribution.Of(frameWork.Select(frame => frame.IntervalMilliseconds)),
                PerformanceDistribution.Of(dispatch.Where(sample => sample.Phase != "generate-fixtures" && sample.LatencyMilliseconds.HasValue).Select(sample => sample.LatencyMilliseconds!.Value)),
                referenceInterval, estimatedMisses, pages, frames, memory.ToArray(), dispatch.ToArray(),
                ["Rendering is a UI callback; it does not prove GPU presentation, monitor refresh, ETW frame timing or input-to-photon latency.",
                 "Estimated missed callbacks use a stated 60 Hz reference and are not measured dropped GPU frames.",
                 "Page first pass may benefit from startup or bounded prefetch; return passes are not guaranteed cache hits.",
                 "Dispatcher samples measure enqueue-to-callback delay from a background producer, not physical keyboard or IME latency.",
                 "Private bytes and working set cover this process; no Python, model request, print job or Sandbox workload runs.",
                 "Idle waits use natural collection only. No forced GC or operating-system standby-cache flush is performed.",
                 "The launcher measures sequential process starts to the ready file. Cold-cache and reference-device budgets require separate user validation."]);
            await File.WriteAllTextAsync(reportPath, JsonSerializer.Serialize(report, PerformanceJson.Default.NativePerformanceReport));
            try { await DisposeLibrarySessionAsync(); } finally { _shutdownFinished = true; Microsoft.UI.Xaml.Application.Current.Exit(); Environment.Exit(exitCode); }
        }
    }

    private static async Task<byte[]> MakePerformancePngAsync(uint width, uint height)
    {
        byte[] pixels = new byte[checked((int)(width * height * 4))];
        for (int y = 0; y < height; y++) for (int x = 0; x < width; x++)
        {
            int index = (y * (int)width + x) * 4;
            byte shade = x > width / 10 && x < width * 9 / 10 && y % 90 < 3 ? (byte)55 : (byte)250;
            pixels[index] = pixels[index + 1] = pixels[index + 2] = shade; pixels[index + 3] = 255;
        }
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore, width, height, 96, 96, pixels); await encoder.FlushAsync();
        using var reader = new DataReader(stream.GetInputStreamAt(0)); await reader.LoadAsync((uint)stream.Size);
        var data = new byte[stream.Size]; reader.ReadBytes(data); return data;
    }
}

internal sealed record PerformanceReady(int ProcessId, DateTimeOffset ReadyUtc, bool Ready, string Boundary);
internal sealed record PerformancePageSample(double ElapsedMilliseconds, string Phase, int Cycle, int PageIndex, uint SourceWidth, uint SourceHeight, double LoadMilliseconds, long CacheBytesBefore, long CacheBytesAfter, bool Displayed);
internal sealed record PerformanceRenderingSample(double ElapsedMilliseconds, string Phase, double IntervalMilliseconds);
internal sealed record PerformanceMemorySample(double ElapsedMilliseconds, string Phase, long PrivateBytes, long WorkingSetBytes, long RasterCacheBytes, double ProcessCpuMilliseconds, long ManagedAllocatedBytes, int Gen0Collections, int Gen1Collections, int Gen2Collections);
internal sealed record PerformanceDispatcherSample(double ElapsedMilliseconds, string Phase, double? LatencyMilliseconds, string? Error);
internal sealed record PerformanceDistribution(int Count, double P50, double P95, double P99, double Maximum, double Mean)
{
    public static PerformanceDistribution Of(IEnumerable<double> values)
    {
        var ordered = values.Order().ToArray();
        double Rank(double percentile) => ordered.Length == 0 ? 0 : ordered[Math.Clamp((int)Math.Ceiling(ordered.Length * percentile) - 1, 0, ordered.Length - 1)];
        return new(ordered.Length, Rank(.5), Rank(.95), Rank(.99), ordered.Length == 0 ? 0 : ordered[^1], ordered.Length == 0 ? 0 : ordered.Average());
    }
}
internal sealed record NativePerformanceReport(DateTimeOffset TimestampUtc, bool Collected, int ProcessId, string SyntheticRoot, string OsVersion, int LogicalProcessors, double RasterizationScale,
    double ScenarioElapsedMilliseconds, Dictionary<string, bool> Checks, string? Error, PerformanceDistribution PageLoadMilliseconds, PerformanceDistribution RenderingCallbackMilliseconds,
    PerformanceDistribution DispatcherLatencyMilliseconds, double EstimatedCallbackReferenceMilliseconds, long EstimatedMissedCallbacks,
    List<PerformancePageSample> Pages, List<PerformanceRenderingSample> Rendering, PerformanceMemorySample[] Memory, PerformanceDispatcherSample[] Dispatcher, string[] MeasurementBoundaries);
[JsonSourceGenerationOptions(WriteIndented = true)]
[JsonSerializable(typeof(NativePerformanceReport))]
[JsonSerializable(typeof(PerformanceReady))]
internal sealed partial class PerformanceJson : JsonSerializerContext;
