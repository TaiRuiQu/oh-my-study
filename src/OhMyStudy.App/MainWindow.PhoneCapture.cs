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
            Content = UiText.Get("Choose files on this computer or scan a QR code to take photos with your phone."),
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
        var restorationPanel = new StackPanel { Spacing = 12 };
        panel.Children.Add(new Expander { Header = UiText.Get("Document restoration"), Content = restorationPanel, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch });
        var restorationStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Text = UiText.Get("Checking local document restoration capabilities…") };
        restorationPanel.Children.Add(restorationStatus);
        restorationPanel.Children.Add(new TextBlock { TextWrapping = TextWrapping.Wrap, Text = UiText.Get("Standard: DocScanner rectification, OpenCV shadow removal and mild defocus recovery. Optional DocRes: 855 MiB of weights plus several GB of isolated Python and CUDA dependencies. Downloads stay on this computer.") });
        var installRestoration = new Button { Content = UiText.Get("Download and install DocRes"), IsEnabled = false };
        var enableRestoration = new CheckBox { Content = UiText.Get("Enable optional DocRes restoration"), IsEnabled = false };
        restorationPanel.Children.Add(installRestoration);
        restorationPanel.Children.Add(enableRestoration);
        CancellationTokenSource? restorationDownload = null;
        async Task RefreshRestorationAsync()
        {
            var capability = await Task.Run(DocResRestoration.Status);
            restorationStatus.Text = capability.Eligible ? UiText.Get(capability.Installed ? "DocRes is installed. Enable it to select neural restoration on your phone." : "DocRes is available for this computer. Download it explicitly to use it.") : UiText.Get("DocRes requires a discrete NVIDIA GPU with more than 12 GiB dedicated VRAM and a working installed CUDA runtime. Exactly 12 GiB is not eligible.");
            installRestoration.IsEnabled = capability.Eligible && !capability.Installed;
            enableRestoration.IsEnabled = capability.Eligible && capability.Installed;
            enableRestoration.IsChecked = capability.Enabled;
            AppLog.Info("DocRes.Capability", $"Eligible={capability.Eligible}; Installed={capability.Installed}; Enabled={capability.Enabled}; Reason={capability.Reason}");
        }
        installRestoration.Click += async (_, _) =>
        {
            if (restorationDownload is not null) { await restorationDownload.CancelAsync(); return; }
            using var canceled = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            restorationDownload = canceled;
            installRestoration.Content = UiText.Get("Cancel DocRes download");
            enableRestoration.IsEnabled = false;
            try
            {
                restorationStatus.Text = UiText.Get("Downloading and checking DocRes locally. This may take several minutes…");
                await DocResRestoration.InstallAsync(line => AppLog.Info("DocRes.Install", line), canceled.Token);
            }
            catch (OperationCanceledException) when (canceled.IsCancellationRequested) { restorationStatus.Text = UiText.Get("DocRes download canceled. Standard restoration remains available."); }
            catch (Exception error)
            {
                AppLog.Error("DocRes.Install", error);
                restorationStatus.Text = UiText.Get("DocRes installation failed. Check the diagnostic log, free disk space, and CUDA installation. Standard restoration remains available.");
            }
            finally
            {
                restorationDownload = null;
                installRestoration.Content = UiText.Get("Download and install DocRes");
                var capability = await Task.Run(DocResRestoration.Status);
                installRestoration.IsEnabled = capability.Eligible && !capability.Installed;
                enableRestoration.IsEnabled = capability.Eligible && capability.Installed;
                if (capability.Installed) restorationStatus.Text = UiText.Get("DocRes is installed. Enable it to select neural restoration on your phone.");
            }
        };
        enableRestoration.Click += (_, _) =>
        {
            try { DocResRestoration.SetEnabled(enableRestoration.IsChecked == true); }
            catch (Exception error) { AppLog.Error("DocRes.Enable", error); enableRestoration.IsChecked = false; }
        };
        panel.Children.Add(new TextBlock
        {
            Text = UiText.Get("Connect your phone to the same Wi-Fi, then scan this code with its camera. No app or account is needed."),
            TextWrapping = TextWrapping.Wrap
        });
        var network = new ComboBox { Header = UiText.Get("Computer network"), ItemsSource = networks, SelectedIndex = 0, HorizontalAlignment = HorizontalAlignment.Stretch };
        panel.Children.Add(network);
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
            Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto },
            PrimaryButtonText = UiText.Get("Import"),
            CloseButtonText = UiText.Get("Cancel"),
            IsPrimaryButtonEnabled = false
        };
        PhoneCaptureServer? server = null;
        bool finished = false, switching = false;
        async Task StartAsync()
        {
            if (switching || finished) return;
            switching = true; network.IsEnabled = false; copy.IsEnabled = false;
            try
            {
                if (server is not null) await server.DisposeAsync();
                server = new(PhonePhotoProcessor.AnalyzeAsync, PhonePhotoProcessor.ProcessAsync, error => AppLog.Error("PhoneCapture.Request", error), () =>
                {
                    var capability = DocResRestoration.Status();
                    return new(capability.Eligible, capability.Installed, capability.Enabled);
                }, () => PhonePhotoProcessor.ProcessingStage);
                var current = server;
                server.Changed += () => DispatcherQueue.TryEnqueue(() =>
                {
                    if (finished || current != server) return;
                    var files = current.Accepted;
                    dialog.IsPrimaryButtonEnabled = files.Count > 0;
                    network.IsEnabled = current.UploadedCount == 0;
                    received.Text = UiText.Format("Received {0} files. Finish taking photos, then click Import.", files.Count) + "\n" + string.Join("\n", files.Select(file => file.Name));
                });
                await server.StartAsync(((PhoneNetwork)network.SelectedItem).Address, html, _lifetime.Token);
                link.Text = server.Url;
                byte[] png = PngByteQRCodeHelper.GetQRCode(server.Url, QRCodeGenerator.ECCLevel.M, 6);
                using var stream = await NativeImageCodec.OpenAsync(png, _lifetime.Token);
                var bitmap = new BitmapImage(); await bitmap.SetSourceAsync(stream); qr.Source = bitmap;
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
        await RefreshRestorationAsync();
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
            if (restorationDownload is not null) await restorationDownload.CancelAsync();
            if (server is not null) await server.DisposeAsync();
            AppLog.Info("PhoneCapture.Stop", "Temporary phone capture session closed");
        }
    }

    private static async Task<string> PhoneCaptureHtmlAsync()
    {
        using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream("OhMyStudy.App.MobileCapture.html")!;
        using var reader = new StreamReader(stream);
        string html = await reader.ReadToEndAsync();
        using var catalog = typeof(MainWindow).Assembly.GetManifestResourceStream("OhMyStudy.App.Localization.en-US.xml")!;
        var translations = XDocument.Load(catalog).Root!.Elements("text")
            .Where(element => ((string)element.Attribute("key")!).StartsWith("PhoneCapture.", StringComparison.Ordinal))
            .ToDictionary(element => element.Value, element => UiText.GetByKey((string)element.Attribute("key")!), StringComparer.Ordinal);
        return html.Replace("__CAPTURE_TEXT__", JsonSerializer.Serialize(translations, PhoneCaptureJsonContext.Default.DictionaryStringString), StringComparison.Ordinal)
            .Replace("__CAPTURE_LANGUAGE__", JsonSerializer.Serialize(UiText.Language, PhoneCaptureJsonContext.Default.String), StringComparison.Ordinal);
    }
}
