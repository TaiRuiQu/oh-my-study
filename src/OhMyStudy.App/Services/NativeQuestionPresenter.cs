using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using OhMyStudy.Documents;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Storage;
using System.Runtime.InteropServices;
using Windows.Storage.Streams;
using UiImage = Microsoft.UI.Xaml.Controls.Image;

namespace OhMyStudy.App.Services;

/// <summary>Creates native reading controls on the caller's UI thread; image and formula work is bounded off-thread.</summary>
public static class NativeQuestionPresenter
{
    private static readonly NativePageImages PageImages = new();
    private static readonly SemaphoreSlim FormulaSlots = new(2, 2);
    private static readonly BoundedPageCache<string, RasterizedPage> FormulaCache = new(16L * 1024 * 1024, 96);

    public static async Task<FrameworkElement> CreateAsync(Question question, LibraryRepository library, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(question);
        ArgumentNullException.ThrowIfNull(library);
        cancellationToken.ThrowIfCancellationRequested();
        // Await without ConfigureAwait(false): all XAML objects belong to this UI thread.
        var panel = new StackPanel { Spacing = 10, HorizontalAlignment = HorizontalAlignment.Stretch };
        await AddQuestionAsync(panel, question, library, cancellationToken);
        return panel;
    }

    private static async Task AddQuestionAsync(StackPanel panel, Question question, LibraryRepository library, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var label = question.Points is { } points ? $"{question.Number}　（{points:g} 分）" : question.Number;
        panel.Children.Add(Text(label, heading: true));
        foreach (var content in question.Content)
        {
            cancellationToken.ThrowIfCancellationRequested();
            switch (content)
            {
                case TextContent text: await AddRichTextAsync(panel, text.Text, cancellationToken); break;
                case FormulaContent formula: panel.Children.Add(await FormulaAsync(formula.Latex, cancellationToken)); break;
                case TableContent table: panel.Children.Add(await TableAsync(table.Rows, cancellationToken)); break;
                case ImageContent image: panel.Children.Add(await SourceImageAsync(image, library, cancellationToken)); break;
            }
        }
        foreach (var option in question.Options) await AddRichTextAsync(panel, option, cancellationToken);
        if (question.Attempt is { } attempt)
        {
            panel.Children.Add(Text("作答：" + (string.IsNullOrWhiteSpace(attempt.StudentAnswer) ? "未作答" : attempt.StudentAnswer)));
            var score = attempt.Grade.Earned is { } earned && attempt.Grade.Possible is { } possible ? $"{earned:g} / {possible:g}" : "分值未确定";
            var source = attempt.Grade.Source switch { GradeSource.Teacher => "教师", GradeSource.Manual => "手动修改", GradeSource.AiReference => "参考答案", _ => "模型解题" };
            panel.Children.Add(Text($"评分：{score} · {source}"));
            if (!string.IsNullOrWhiteSpace(attempt.Grade.Reason)) panel.Children.Add(Text(attempt.Grade.Reason));
            if (attempt.Teacher is { } teacher && !string.IsNullOrWhiteSpace(teacher.Raw)) panel.Children.Add(Text("教师批注：" + teacher.Raw));
            if (!string.IsNullOrWhiteSpace(attempt.Conflict)) panel.Children.Add(Text("判断差异：" + attempt.Conflict));
            if (!string.IsNullOrWhiteSpace(attempt.AiSolution)) panel.Children.Add(Text("解题过程：" + attempt.AiSolution));
        }
        foreach (var uncertain in question.Uncertainties) panel.Children.Add(Text("待确认：" + uncertain));
        foreach (var part in question.Parts)
        {
            var nested = new StackPanel { Spacing = 8, Margin = new Thickness(16, 4, 0, 4) };
            await AddQuestionAsync(nested, part, library, cancellationToken);
            panel.Children.Add(nested);
        }
    }

    private static TextBlock Text(string text, bool heading = false) => new()
    {
        Text = text,
        TextWrapping = TextWrapping.Wrap,
        IsTextSelectionEnabled = true,
        FontFamily = new FontFamily("ms-appx:///native/fonts/" + TypstToolchain.FontFileName + "#" + TypstToolchain.TextFontFamily),
        FontSize = heading ? 16 : TypstToolchain.BodySizePoints * 96 / 72,
        FontWeight = heading ? FontWeights.SemiBold : FontWeights.Normal,
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static async Task AddRichTextAsync(StackPanel panel, string text, CancellationToken cancellationToken)
    {
        foreach (var run in DocumentText.Split(text))
            panel.Children.Add(run.IsFormula ? await FormulaAsync(run.Text, cancellationToken) : Text(run.Text));
    }

    private static async Task<FrameworkElement> TableAsync(List<List<string>> rows, CancellationToken cancellationToken)
    {
        var grid = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
        int columns = rows.Count == 0 ? 0 : rows.Max(row => row.Count);
        for (var column = 0; column < columns; column++) grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto, MinWidth = 72, MaxWidth = 320 });
        for (var row = 0; row < rows.Count; row++)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            for (var column = 0; column < columns; column++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var body = new StackPanel { Spacing = 4, MaxWidth = 300 };
                await AddRichTextAsync(body, column < rows[row].Count ? rows[row][column] : "", cancellationToken);
                var cell = new Border
                {
                    Padding = new Thickness(10, 7, 10, 7),
                    BorderThickness = new Thickness(0, 0, 0, 1),
                    Child = body
                };
                if (Microsoft.UI.Xaml.Application.Current.Resources.TryGetValue("ControlStrokeColorDefaultBrush", out var stroke) && stroke is Brush brush) cell.BorderBrush = brush;
                Grid.SetRow(cell, row); Grid.SetColumn(cell, column); grid.Children.Add(cell);
            }
        }
        AutomationProperties.SetName(grid, "表格");
        return new ScrollViewer { Content = grid, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, HorizontalScrollMode = ScrollMode.Enabled, VerticalScrollMode = ScrollMode.Disabled };
    }

    private static async Task<FrameworkElement> SourceImageAsync(ImageContent content, LibraryRepository library, CancellationToken cancellationToken)
    {
        try
        {
            PageViewportTransform.ValidateRegion(new PageRect(content.Region.X, content.Region.Y, content.Region.Width, content.Region.Height));
            var documents = await library.ListDocumentsAsync(cancellationToken);
            var page = documents.SelectMany(document => document.Pages).SingleOrDefault(page => page.Id == content.Region.PageId);
            if (page is null) return Text("图片暂时无法显示。");
            var source = await library.ReadAssetAsync(page.AssetId, cancellationToken: cancellationToken);
            var result = await PageImages.ReadAsync(source.Bytes, content.Region, page.Rotation, cancellationToken);
            var image = await ImageAsync(Convert.FromBase64String(result.Image.Base64), content.Caption, cancellationToken);
            image.MaxHeight = 420;
            if (string.IsNullOrWhiteSpace(content.Caption)) return image;
            var panel = new StackPanel { Spacing = 5 };
            panel.Children.Add(image); panel.Children.Add(Text(content.Caption)); return panel;
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or COMException or UnauthorizedAccessException)
        { return Text("图片暂时无法显示。"); }
    }

    private static async Task<FrameworkElement> FormulaAsync(string latex, CancellationToken cancellationToken)
    {
        try
        {
            var raster = await FormulaRasterAsync(latex, cancellationToken);
            if (raster is null) return Text(latex);
            var host = new Grid { HorizontalAlignment = HorizontalAlignment.Left };
            var original = Text(latex);
            original.Visibility = Visibility.Collapsed;
            var image = new UiImage
            {
                Width = Math.Min(720, Math.Max(16, raster.PageWidthPoints * 96d / 72)),
                MaxWidth = 720,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Left
            };
            AutomationProperties.SetName(image, latex);
            host.Children.Add(image); host.Children.Add(original);
            var colors = new Windows.UI.ViewManagement.UISettings();
            CancellationTokenSource? painting = null;
            int revision = 0;

            async Task RepaintAsync()
            {
                if (cancellationToken.IsCancellationRequested) return;
                int current = ++revision;
                painting?.Cancel();
                using var repaint = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                painting = repaint;
                try
                {
                    // The hidden selectable original inherits the same native foreground as ordinary text.
                    var color = original.Foreground is SolidColorBrush brush ? brush.Color
                        : colors.GetColorValue(Windows.UI.ViewManagement.UIColorType.Foreground);
                    byte[] png = await NativeImageWork.RunAsync(async () =>
                    {
                        var pixels = new byte[checked(raster.Width * raster.Height * 4)];
                        for (int row = 0; row < raster.Height; row++)
                        {
                            repaint.Token.ThrowIfCancellationRequested();
                            for (int column = 0; column < raster.Width; column++)
                            {
                                int source = row * raster.Stride + column * 4;
                                int target = (row * raster.Width + column) * 4;
                                int darkness = 255 - Math.Max(raster.Bgra32[source], Math.Max(raster.Bgra32[source + 1], raster.Bgra32[source + 2]));
                                pixels[target] = color.B; pixels[target + 1] = color.G; pixels[target + 2] = color.R;
                                pixels[target + 3] = (byte)((darkness * color.A + 127) / 255);
                            }
                        }
                        return await NativeImageCodec.EncodePngAsync(pixels, (uint)raster.Width, (uint)raster.Height, repaint.Token).ConfigureAwait(false);
                    }, repaint.Token);
                    var colored = await ImageAsync(png, latex, repaint.Token);
                    if (current != revision || repaint.IsCancellationRequested) return;
                    image.Source = colored.Source; image.Visibility = Visibility.Visible; original.Visibility = Visibility.Collapsed;
                }
                catch (OperationCanceledException) { }
                catch (Exception)
                {
                    if (current == revision && !cancellationToken.IsCancellationRequested)
                    { image.Visibility = Visibility.Collapsed; original.Visibility = Visibility.Visible; }
                }
                finally { if (ReferenceEquals(painting, repaint)) painting = null; }
            }

            void SystemColorsChanged(Windows.UI.ViewManagement.UISettings sender, object args)
                => host.DispatcherQueue.TryEnqueue(async () => { if (host.IsLoaded) await RepaintAsync(); });
            host.Loaded += async (_, _) =>
            {
                colors.ColorValuesChanged -= SystemColorsChanged;
                colors.ColorValuesChanged += SystemColorsChanged;
                await RepaintAsync();
            };
            host.ActualThemeChanged += async (_, _) => { if (host.IsLoaded) await RepaintAsync(); };
            host.Unloaded += (_, _) =>
            {
                colors.ColorValuesChanged -= SystemColorsChanged;
                revision++; painting?.Cancel();
            };
            await RepaintAsync();
            cancellationToken.ThrowIfCancellationRequested();
            return host;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { return Text(latex); }
        catch (OperationCanceledException) { throw; }
        catch (Exception error) when (error is IOException or ArgumentException or InvalidOperationException or COMException or UnauthorizedAccessException or DllNotFoundException or BadImageFormatException or System.ComponentModel.Win32Exception or TimeoutException)
        { return Text(latex); }
    }

    private static async Task<RasterizedPage?> FormulaRasterAsync(string latex, CancellationToken cancellationToken)
    {
        if (latex.Length > LatexFormulaConverter.MaximumInputLength) return null;
        if (FormulaCache.TryGet(latex, out var cached)) return cached;
        await FormulaSlots.WaitAsync(cancellationToken);
        try
        {
            if (FormulaCache.TryGet(latex, out cached)) return cached;
            return await Task.Run<RasterizedPage?>(async () =>
            {
                if (!LatexFormulaConverter.Convert(latex).IsSupported) return null;
                var tools = new TypstToolchain(Path.Combine(AppContext.BaseDirectory, "native", "typst", "typst.exe"), Path.Combine(AppContext.BaseDirectory, "native", "fonts"));
                var service = new TypstDocumentService(tools);
                string directory = Directory.CreateTempSubdirectory("OhMyStudy-Formula-").FullName;
                try
                {
                    var preview = await service.RenderFormulaAsync(latex, directory, cancellationToken).ConfigureAwait(false);
                    var rasterizer = new PdfiumRasterizer(Path.Combine(AppContext.BaseDirectory, "native", "pdfium.dll"));
                    using var document = rasterizer.Open(preview.PdfPath);
                    var result = document.RenderPage(0, maximumDimension: 1600, cancellationToken: cancellationToken);
                    cancellationToken.ThrowIfCancellationRequested();
                    FormulaCache.Set(latex, result, result.Bgra32.LongLength);
                    return result;
                }
                finally
                {
                    // Delete only outputs of this exact fresh directory; formulas are not retained on disk.
                    try
                    {
                        foreach (var name in new[] { "formula.typ", "formula.pdf", "formula.svg" }) File.Delete(Path.Combine(directory, name));
                        string packages = Path.Combine(directory, "empty-packages");
                        if (Directory.Exists(packages)) Directory.Delete(packages, recursive: false);
                        Directory.Delete(directory, recursive: false);
                    }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }, cancellationToken);
        }
        finally { FormulaSlots.Release(); }
    }

    private static async Task<UiImage> ImageAsync(byte[] png, string accessibleText, CancellationToken cancellationToken)
    {
        using var stream = new InMemoryRandomAccessStream();
        using (var writer = new DataWriter(stream))
        {
            writer.WriteBytes(png); await writer.StoreAsync().AsTask(cancellationToken); writer.DetachStream();
        }
        stream.Seek(0);
        var bitmap = new BitmapImage();
        await bitmap.SetSourceAsync(stream).AsTask(cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        var image = new UiImage { Source = bitmap, Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Left, MaxWidth = 720 };
        AutomationProperties.SetName(image, string.IsNullOrWhiteSpace(accessibleText) ? "题目图片" : accessibleText);
        return image;
    }
}
