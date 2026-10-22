using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.Services;
using OhMyStudy.App.ViewModels;

namespace OhMyStudy.App.Controls;

/// <summary>Only realized rows request previews. Visible requests share a bounded batch and the reading caches.</summary>
public sealed partial class QuestionPreview : Grid
{
    public static readonly DependencyProperty ItemProperty = DependencyProperty.Register(nameof(Item), typeof(QuestionViewModel), typeof(QuestionPreview),
        new PropertyMetadata(null, (sender, _) => ((QuestionPreview)sender).Schedule()));
    public QuestionViewModel? Item { get => (QuestionViewModel?)GetValue(ItemProperty); set => SetValue(ItemProperty, value); }
    private CancellationTokenSource? _render;
    private int _generation;
    private bool _nearViewport;
    private Windows.Foundation.Rect _viewport;
    internal Task Ready { get; private set; } = Task.CompletedTask;
    internal string? RenderedSource { get; private set; }

    public QuestionPreview()
    {
        Height = 48;
        HorizontalAlignment = HorizontalAlignment.Stretch;
        Loaded += (_, _) => { _nearViewport = IsNearViewport(); Schedule(); };
        Unloaded += (_, _) => { _generation++; _render?.Cancel(); _render = null; _nearViewport = false; };
        SizeChanged += (_, args) => { if (Math.Abs(args.NewSize.Width - args.PreviousSize.Width) >= 1) Schedule(); };
        EffectiveViewportChanged += (_, args) =>
        {
            _viewport = args.EffectiveViewport;
            bool near = IsNearViewport();
            if (near == _nearViewport) return;
            _nearViewport = near;
            Schedule();
        };
    }

    private bool IsNearViewport() => _viewport.Width > 0 && _viewport.Height > 0 && _viewport.X < ActualWidth
        && _viewport.X + _viewport.Width > 0 && _viewport.Y < ActualHeight + 96 && _viewport.Y + _viewport.Height > -96;

    private void Schedule()
    {
        _generation++;
        _render?.Cancel();
        if (!IsLoaded || !_nearViewport || Item is null || ActualWidth < 64) return;
        Height = NativeReadingSession.CachedPreviewHeight(Item.PreviewText, Math.Floor(ActualWidth)) ?? 48;
        Ready = RenderAsync(Item, Math.Floor(ActualWidth), _generation);
    }

    private async Task RenderAsync(QuestionViewModel item, double width, int generation)
    {
        using var cancellation = new CancellationTokenSource();
        _render = cancellation;
        var token = cancellation.Token;
        Children.Clear();
        Children.Add(new TextBlock { Text = UiText.Get("Loading…"), FontSize = 14, Opacity = .7 });
        try
        {
            var session = NativeReadingSession.CachedPreviewHeight(item.PreviewText, width) is not null
                ? await NativeReadingSession.CreatePreviewsAsync(new[] { item.PreviewText }, width, token)
                : await EnqueueAsync(item.PreviewText, width, token);
            var body = await session.PresentAsync(item.PreviewText, token, theme: ActualTheme);
            if (generation != _generation || token.IsCancellationRequested || !ReferenceEquals(item, Item)) return;
            Children.Clear(); Children.Add(body); RenderedSource = item.PreviewText;
            Height = NativeReadingSession.CachedPreviewHeight(item.PreviewText, width) ?? 20;
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            AppLog.Error("Question.Preview", error);
            if (generation == _generation)
            {
                Children.Clear(); Children.Add(new TextBlock { Text = UiText.Get("Preview unavailable"), FontSize = 14 });
            }
        }
        finally { if (ReferenceEquals(_render, cancellation)) _render = null; }
    }

    private sealed record Request(string Text, double Width, CancellationToken Token, TaskCompletionSource<NativeReadingSession> Completion);
    private static readonly List<Request> Pending = [];
    private static bool _scheduled;

    private static Task<NativeReadingSession> EnqueueAsync(string text, double width, CancellationToken token)
    {
        var completion = new TaskCompletionSource<NativeReadingSession>(TaskCreationOptions.RunContinuationsAsynchronously);
        Pending.Add(new(text, width, token, completion));
        if (!_scheduled) { _scheduled = true; _ = FlushAsync(); }
        return completion.Task.WaitAsync(token);
    }

    private static async Task FlushAsync()
    {
        // A selected question starts first. List rows settle to their actual width before sharing one compiler invocation.
        await Task.Delay(60);
        var requests = Pending.ToArray(); Pending.Clear(); _scheduled = false;
        foreach (var group in requests.Where(request => !request.Token.IsCancellationRequested).GroupBy(request => request.Width))
        {
            foreach (var batch in group.Chunk(48))
            {
                using var cancellation = new CancellationTokenSource();
                int interested = batch.Length;
                var registrations = batch.Select(request => request.Token.Register(() =>
                {
                    if (Interlocked.Decrement(ref interested) == 0) cancellation.Cancel();
                })).ToArray();
                try
                {
                    var session = await NativeReadingSession.CreatePreviewsAsync(batch.Select(request => request.Text).ToArray(), group.Key, cancellation.Token);
                    foreach (var request in batch) request.Completion.TrySetResult(session);
                }
                catch (OperationCanceledException)
                { foreach (var request in batch) request.Completion.TrySetCanceled(); }
                catch (Exception error)
                { foreach (var request in batch) request.Completion.TrySetException(error); }
                finally { foreach (var registration in registrations) registration.Dispose(); }
            }
        }
    }
}
