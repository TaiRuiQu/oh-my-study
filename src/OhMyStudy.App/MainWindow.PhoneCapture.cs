using System.Text.Json;
using System.Xml.Linq;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using OhMyStudy.App.Diagnostics;
using OhMyStudy.App.Services;
using OhMyStudy.Documents;
using OhMyStudy.Infrastructure.Import;
using OhMyStudy.Infrastructure.Storage;
using QRCoder;
using Windows.ApplicationModel.DataTransfer;

namespace OhMyStudy.App;

public sealed partial class MainWindow
{
    private async Task PickAndImportAsync(bool reference = false, string? documentId = null, bool studentAnswers = false)
    {
        var choice = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = UiText.Get("Import from"),
            PrimaryButtonText = UiText.Get("Choose files"),
            SecondaryButtonText = UiText.Get("Take photos with phone"),
            CloseButtonText = UiText.Get("Cancel")
        };
        var result = await choice.ShowAsync();
        if (result == ContentDialogResult.Primary)
            await ImportFilesAsync(await PickFilesAsync(), reference, documentId, studentAnswers: studentAnswers);
        else if (result == ContentDialogResult.Secondary)
        {
            var sources = await ReceivePhoneFilesAsync();
            if (sources.Count > 0) await ImportFilesAsync([], reference, documentId, studentAnswers: studentAnswers, captured: sources);
        }
    }

    private async Task<IReadOnlyList<ImportSource>> ReceivePhoneFilesAsync(IReadOnlyList<PhoneNetwork>? diagnosticNetworks = null)
    {
        var networks = diagnosticNetworks ?? PhoneCaptureServer.Networks();
        if (networks.Count == 0)
        {
            ViewModel.StatusMessage = UiText.Get("Connect this computer to Wi-Fi or Ethernet, then try phone capture again.");
            return [];
        }
        string html = await PhoneCaptureHtmlAsync();
        var panel = new StackPanel { Spacing = 12, MaxWidth = 480 };
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Image correction runs locally with the bundled trained models."),
            TextWrapping = TextWrapping.Wrap
        });
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Phone and computer must use the same Wi-Fi network."),
            TextWrapping = TextWrapping.Wrap
        });
        var network = new ComboBox { Header = UiText.Get("Computer network"), ItemsSource = networks, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(network);
        var connectionMode = new ComboBox
        {
            Header = UiText.Get("Phone connection"),
            ItemsSource = new[] { UiText.Get("Continuous camera (experimental HTTPS)"), UiText.Get("System camera and files (HTTP)") },
            SelectedIndex = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        panel.Children.Add(connectionMode);
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Experimental HTTPS uses the disclosed eMudhra issuer. Browsers may reject this revoked chain or require Certificate Transparency. If it cannot open, choose System camera and files (HTTP)."),
            TextWrapping = TextWrapping.Wrap
        });
        var qr = new Microsoft.UI.Xaml.Controls.Image { Width = 240, Height = 240, HorizontalAlignment = HorizontalAlignment.Center };
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(qr, UiText.Get("Scan to connect your phone"));
        panel.Children.Add(qr);
        var link = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Header = UiText.Get("Connection link") };
        panel.Children.Add(link);
        var copy = new Button { Content = UiText.Get("Copy link") };
        copy.Click += (_, _) => { var data = new DataPackage(); data.SetText(link.Text); Clipboard.SetContent(data); };
        panel.Children.Add(copy);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(status);
        var received = new TextBlock { TextWrapping = TextWrapping.Wrap };
        panel.Children.Add(received);
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Cannot connect? Allow Oh My Study on private networks if Windows asks. Avoid guest Wi-Fi; try a phone hotspot or select another computer network above. Local HTTP is intended for trusted networks."),
            TextWrapping = TextWrapping.Wrap
        });
        var dialog = new ContentDialog
        {
            XamlRoot = RootGrid.XamlRoot,
            Title = UiText.Get("Take photos with phone"),
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                HorizontalScrollMode = ScrollMode.Disabled,
                Padding = new Thickness(0, 0, 16, 0)
            },
            PrimaryButtonText = UiText.Get("Import"),
            CloseButtonText = UiText.Get("Cancel"),
            IsPrimaryButtonEnabled = false
        };
        PhoneCaptureServer? server = null;
        PhoneCaptureCertificate? certificate = null;
        bool finished = false, switching = false;
        async Task UpdateLinkAsync()
        {
            if (server is null) return;
            link.Text = connectionMode.SelectedIndex == 0 ? server.Url : server.HttpUrl;
            byte[] png = PngByteQRCodeHelper.GetQRCode(link.Text, QRCodeGenerator.ECCLevel.M, 6);
            using var stream = await NativeImageCodec.OpenAsync(png, _lifetime.Token);
            var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); qr.Source = bitmap;
        }
        connectionMode.SelectionChanged += async (_, _) =>
        {
            try { await UpdateLinkAsync(); }
            catch (Exception error) { AppLog.Error("PhoneCapture.Link", error); }
        };
        async Task StartAsync()
        {
            if (switching || finished) return;
            switching = true; network.IsEnabled = false; copy.IsEnabled = false;
            try
            {
                if (server is not null) await server.DisposeAsync();
                certificate?.Dispose();
                certificate = new PhoneCaptureCertificate(((PhoneNetwork)network.SelectedItem).Address);
                server = new(StudyImageProcessor.PreviewAsync, StudyImageProcessor.ProcessAsync, error => AppLog.Error("PhoneCapture.Request", error),
                    () => StudyImageProcessor.ProcessingStage, scripts: PhoneCaptureScripts());
                var current = server;
                server.Changed += () => DispatcherQueue.TryEnqueue(() =>
                {
                    if (finished || current != server) return;
                    var files = current.Accepted;
                    dialog.IsPrimaryButtonEnabled = files.Count > 0;
                    network.IsEnabled = current.UploadedCount == 0;
                    received.Text = UiText.Format("Received {0} files.", files.Count) + "\n" + string.Join("\n", files.Select(file => file.Name));
                });
                await server.StartAsync(((PhoneNetwork)network.SelectedItem).Address, html, _lifetime.Token, certificate.Certificate, certificate.Chain);
                await UpdateLinkAsync();
                status.Text = UiText.Format("Waiting for your phone. This code expires at {0:t}.", server.ExpiresAt.LocalDateTime);
                copy.IsEnabled = true;
                AppLog.Info("PhoneCapture.Start", $"Address={((PhoneNetwork)network.SelectedItem).Address}; Expires={server.ExpiresAt:O}");
            }
            catch (Exception error)
            {
                AppLog.Error("PhoneCapture.Start", error);
                qr.Source = null; link.Text = "";
                status.Text = UiText.Get("Could not start phone capture. Check your network or select another network above.");
            }
            finally { switching = false; if (!finished) network.IsEnabled = true; }
        }
        network.SelectionChanged += async (_, _) => await StartAsync();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(10) };
        timer.Tick += (_, _) =>
        {
            if (server is not null && DateTimeOffset.Now >= server.ExpiresAt)
            {
                status.Text = UiText.Get("This code has expired. Import received files, or close and reopen phone capture for a new code.");
                timer.Stop();
            }
        };
        await StartAsync();
        timer.Start();
        try
        {
            var result = await dialog.ShowAsync();
            return result == ContentDialogResult.Primary && server is not null ? await server.TakeFilesAsync(_lifetime.Token) : [];
        }
        finally
        {
            finished = true; timer.Stop();
            if (server is not null) await server.DisposeAsync();
            certificate?.Dispose();
            AppLog.Info("PhoneCapture.Stop", "Temporary phone capture session closed");
        }
    }

    private static IReadOnlyDictionary<string, byte[]> PhoneCaptureScripts()
    {
        var scripts = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        foreach (string name in new[] { "capture.js" })
        {
            using var resource = typeof(MainWindow).Assembly.GetManifestResourceStream("OhMyStudy.App.MobileCapture." + name)!;
            byte[] bytes = new byte[checked((int)resource.Length)];
            resource.ReadExactly(bytes);
            scripts.Add("/" + name, bytes);
        }
        return scripts;
    }

    private static async Task<string> PhoneCaptureHtmlAsync()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("OhMyStudy.App.MobileCapture.html")!;
        using var reader = new StreamReader(stream);
        string html = await reader.ReadToEndAsync();
        using var font = typeof(MainWindow).Assembly.GetManifestResourceStream("OhMyStudy.App.MobileCapture.Font")!;
        using var fontBytes = new MemoryStream();
        await font.CopyToAsync(fontBytes);
        html = html.Replace("__CAPTURE_FONT__", Convert.ToBase64String(fontBytes.ToArray()), StringComparison.Ordinal);
        using var catalog = typeof(MainWindow).Assembly.GetManifestResourceStream("OhMyStudy.App.Localization.en-US.xml")!;
        var translations = XDocument.Load(catalog).Root!.Elements("text")
            .Where(element => ((string)element.Attribute("key")!).StartsWith("PhoneCapture.", StringComparison.Ordinal))
            .ToDictionary(element => element.Value, element => UiText.GetByKey((string)element.Attribute("key")!), StringComparer.Ordinal);
        return html.Replace("__CAPTURE_TEXT__", JsonSerializer.Serialize(translations, PhoneCaptureJsonContext.Default.DictionaryStringString), StringComparison.Ordinal)
            .Replace("__CAPTURE_LANGUAGE__", JsonSerializer.Serialize(UiText.Language, PhoneCaptureJsonContext.Default.String), StringComparison.Ordinal);
    }
}
