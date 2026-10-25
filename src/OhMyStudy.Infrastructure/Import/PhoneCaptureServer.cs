using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OhMyStudy.Infrastructure.Storage;

namespace OhMyStudy.Infrastructure.Import;

public sealed record PhoneNetwork(string Name, IPAddress Address)
{
    public override string ToString() => $"{Name} · {Address}";
}

public sealed record PhotoProcessingOptions(bool Crop = false, bool Rectify = false, bool Shadows = false, bool Deblur = false, bool Appearance = false, bool Grayscale = false, bool AutoOrientation = false);
public sealed record PhotoCorrection(double[][] Corners, string Enhancement, ushort Rotation = 0, string Backend = "standard", string[]? Tasks = null, PhotoProcessingOptions? Processing = null);
public sealed record PhonePhotoAnalysis(byte[] PreviewPng, double[][] Corners, bool Detected, ushort Rotation = 0, double OrientationConfidence = 0);
public sealed record PhonePhotoInfo(string Id, string Name, bool Pdf, double[][]? Corners, bool Detected, bool Ready = false, ushort Rotation = 0, double OrientationConfidence = 0, bool Processed = false, PhotoCorrection? Correction = null);
public sealed record PhoneCaptureSelection(string[] Ids);
public sealed record PhoneRestorationStatus(bool Eligible, bool Installed, bool Enabled);
public sealed record PhoneCaptureStatus(IReadOnlyList<PhonePhotoInfo> Files, PhoneRestorationStatus? Restoration = null);
public sealed class PhotoRestorationException(string message, Exception innerException) : Exception(message, innerException);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(PhotoCorrection))]
[JsonSerializable(typeof(PhonePhotoInfo))]
[JsonSerializable(typeof(PhoneCaptureStatus))]
[JsonSerializable(typeof(PhoneCaptureSelection))]
[JsonSerializable(typeof(Dictionary<string, string>))]
[JsonSerializable(typeof(string))]
public partial class PhoneCaptureJsonContext : JsonSerializerContext;

public sealed class PhoneCaptureServer : IAsyncDisposable
{
    private sealed class CaptureLogProvider(Action<Exception> log) : ILoggerProvider
    {
        private sealed class CaptureLogger(string category, Action<Exception> log) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
            public bool IsEnabled(LogLevel level) => level >= LogLevel.Warning || category.EndsWith("HttpsConnectionMiddleware", StringComparison.Ordinal);
            public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            {
                if (IsEnabled(level) && exception is not null) log(new IOException($"{category}: {formatter(state, exception)}", exception));
            }
        }
        public ILogger CreateLogger(string categoryName) => new CaptureLogger(categoryName, log);
        public void Dispose() { }
    }
    private sealed record Upload(PhonePhotoInfo Info, byte[] Original, byte[]? Preview, byte[]? Processed, bool Accepted, long Order);
    private readonly Dictionary<string, Upload> _uploads = [];
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _expiration = new(TimeSpan.FromMinutes(20));
    private readonly string _token = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
    private readonly Func<byte[], CancellationToken, Task<PhonePhotoAnalysis>> _preview;
    private readonly Func<byte[], PhotoCorrection, CancellationToken, Task<byte[]>> _process;
    private readonly Action<Exception> _logError;
    private readonly Func<PhoneRestorationStatus>? _restoration;
    private readonly Func<string>? _processingStage;
    private readonly IReadOnlyDictionary<string, byte[]> _scripts;
    private WebApplication? _app;
    private string _html = "";
    private PhonePhotoInfo[] _accepted = [];
    private int _uploadedCount;
    private long _nextOrder;
    public string Url { get; private set; } = "";
    public string HttpUrl { get; private set; } = "";
    public DateTimeOffset ExpiresAt { get; } = DateTimeOffset.Now.AddMinutes(20);
    public IReadOnlyList<PhonePhotoInfo> Accepted => Volatile.Read(ref _accepted);
    public int UploadedCount => Volatile.Read(ref _uploadedCount);
    public event Action? Changed;

    public PhoneCaptureServer(Func<byte[], CancellationToken, Task<PhonePhotoAnalysis>> analyze,
        Func<byte[], PhotoCorrection, CancellationToken, Task<byte[]>> process, Action<Exception> logError, Func<PhoneRestorationStatus>? restoration = null, Func<string>? processingStage = null, Func<byte[], CancellationToken, Task<PhonePhotoAnalysis>>? preview = null, IReadOnlyDictionary<string, byte[]>? scripts = null)
    {
        _process = process; _logError = logError; _restoration = restoration; _processingStage = processingStage;
        _preview = preview ?? analyze;
        _scripts = scripts ?? new Dictionary<string, byte[]>();
    }

    public static IReadOnlyList<PhoneNetwork> Networks() => NetworkInterface.GetAllNetworkInterfaces()
        .Where(n => n.OperationalStatus == OperationalStatus.Up && n.NetworkInterfaceType is NetworkInterfaceType.Wireless80211 or NetworkInterfaceType.Ethernet)
        .OrderBy(n => n.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ? 0 : 1)
        .ThenBy(n => n.GetIPProperties().GatewayAddresses.Count == 0 ? 1 : 0)
        .SelectMany(n => n.GetIPProperties().UnicastAddresses.Where(a => a.Address.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a.Address)
            && !a.Address.ToString().StartsWith("169.254.", StringComparison.Ordinal)).Select(a => new PhoneNetwork(n.Name, a.Address))).ToArray();

    public async Task StartAsync(IPAddress address, string html, CancellationToken cancellationToken = default, X509Certificate2? certificate = null, X509Certificate2Collection? chain = null)
    {
        _html = html;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { Args = [], ContentRootPath = AppContext.BaseDirectory });
        builder.Logging.ClearProviders();
        builder.Logging.SetMinimumLevel(LogLevel.Debug);
        builder.Logging.AddProvider(new CaptureLogProvider(_logError));
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.Listen(address, 0, endpoint =>
            {
                if (certificate is not null) endpoint.UseHttps(https =>
                {
                    https.ServerCertificate = certificate;
                    https.ServerCertificateChain = chain;
                });
            });
            if (certificate is not null) options.Listen(address, 0);
            options.Limits.MaxRequestBodySize = LibraryLimits.MaximumAssetBytes;
            options.Limits.MaxConcurrentConnections = 4;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(15);
        });
        _app = builder.Build();
        _app.Run(HandleAsync);
        await _app.StartAsync(cancellationToken).ConfigureAwait(false);
        var endpoints = _app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        string endpoint = endpoints.Single(value => value.StartsWith(certificate is null ? "http:" : "https:", StringComparison.Ordinal));
        Url = endpoint + "/#" + _token;
        HttpUrl = endpoints.Single(value => value.StartsWith("http:", StringComparison.Ordinal)) + "/#" + _token;
    }

    private async Task HandleAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.XContentTypeOptions = "nosniff";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
        context.Response.Headers["Content-Security-Policy"] = "default-src 'none'; script-src 'self'; worker-src 'self'; style-src 'unsafe-inline'; font-src data:; img-src 'self' blob: data:; media-src 'self' blob:; connect-src 'self'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'";
        context.Response.Headers["Permissions-Policy"] = "camera=(self), microphone=()";
        if (_expiration.IsCancellationRequested) { context.Response.StatusCode = 410; return; }
        if (context.Request.Path == "/" && HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.ContentType = "text/html; charset=utf-8";
            await context.Response.WriteAsync(_html, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        if (HttpMethods.IsGet(context.Request.Method) && _scripts.TryGetValue(context.Request.Path.Value ?? "", out byte[]? script))
        {
            context.Response.ContentType = "text/javascript; charset=utf-8";
            await context.Response.Body.WriteAsync(script, context.RequestAborted).ConfigureAwait(false);
            return;
        }
        string provided = context.Request.Headers["X-Capture-Token"].ToString();
        if (provided.Length != _token.Length || !CryptographicOperations.FixedTimeEquals(System.Text.Encoding.ASCII.GetBytes(provided), System.Text.Encoding.ASCII.GetBytes(_token)))
        { context.Response.StatusCode = 401; return; }
        using var canceled = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _expiration.Token);
        if (context.Request.Path == "/progress" && HttpMethods.IsGet(context.Request.Method))
        {
            context.Response.ContentType = "text/plain; charset=utf-8";
            await context.Response.WriteAsync(_processingStage?.Invoke() ?? "", canceled.Token).ConfigureAwait(false);
            return;
        }
        bool entered = false;
        string requestDetails = $"{context.Request.Method} {context.Request.Path}";
        try
        {
            await _gate.WaitAsync(canceled.Token).ConfigureAwait(false); entered = true;
            string path = context.Request.Path.Value ?? "";
            if (HttpMethods.IsGet(context.Request.Method) && path == "/status")
            {
                context.Response.ContentType = "application/json";
                var files = _uploads.Values.OrderBy(upload => upload.Order).Select(upload => upload.Info with { Ready = upload.Accepted, Processed = upload.Processed is not null }).ToArray();
                await JsonSerializer.SerializeAsync(context.Response.Body, new PhoneCaptureStatus(files, _restoration?.Invoke()), PhoneCaptureJsonContext.Default.PhoneCaptureStatus, canceled.Token).ConfigureAwait(false);
            }
            else if (HttpMethods.IsPost(context.Request.Method) && path == "/upload")
            {
                if (_uploads.Count >= LibraryLimits.MaximumPages) throw new InvalidDataException("The session already contains 12 files");
                byte[] bytes = await ReadBodyAsync(context.Request, LibraryLimits.MaximumAssetBytes, canceled.Token).ConfigureAwait(false);
                if (RetainedBytes + bytes.LongLength > LibraryLimits.MaximumBatchBytes) throw new InvalidDataException("The session exceeds 256 MiB");
                bool pdf = bytes.AsSpan().StartsWith("%PDF-"u8);
                string name = Path.GetFileName(context.Request.Query["name"].ToString());
                if (string.IsNullOrWhiteSpace(name) || name.Length > 180) name = pdf ? "Phone.pdf" : "Photo.jpg";
                var analysis = pdf ? null : await _preview(bytes, canceled.Token).ConfigureAwait(false);
                if (RetainedBytes + bytes.LongLength + (analysis?.PreviewPng.LongLength ?? 0) > LibraryLimits.MaximumBatchBytes)
                    throw new InvalidDataException("Photo previews exceed 256 MiB");
                var info = new PhonePhotoInfo(Guid.NewGuid().ToString("N"), name, pdf, analysis?.Corners, analysis?.Detected ?? false,
                    Rotation: analysis?.Rotation ?? 0, OrientationConfidence: analysis?.OrientationConfidence ?? 0);
                _uploads.Add(info.Id, new(info, bytes, analysis?.PreviewPng, null, false, _nextOrder++));
                Publish();
                context.Response.ContentType = "application/json";
                await JsonSerializer.SerializeAsync(context.Response.Body, info, PhoneCaptureJsonContext.Default.PhonePhotoInfo, canceled.Token).ConfigureAwait(false);
            }
            else if (HttpMethods.IsPost(context.Request.Method) && path == "/reorder")
            {
                byte[] body = await ReadBodyAsync(context.Request, 4096, canceled.Token).ConfigureAwait(false);
                requestDetails += $"; Order={System.Text.Encoding.UTF8.GetString(body)}";
                var selection = JsonSerializer.Deserialize(body, PhoneCaptureJsonContext.Default.PhoneCaptureSelection);
                if (selection?.Ids is not { Length: > 0 and <= LibraryLimits.MaximumPages } ids || ids.Length != _uploads.Values.Count(upload => !upload.Accepted)
                    || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length || ids.Any(id => string.IsNullOrWhiteSpace(id) || !_uploads.TryGetValue(id, out var upload) || upload.Accepted))
                    throw new InvalidDataException("Provide every unconfirmed file exactly once to reorder pages");
                foreach (string id in ids) _uploads[id] = _uploads[id] with { Order = _nextOrder++ };
                Publish();
            }
            else if (HttpMethods.IsPost(context.Request.Method) && path == "/accept-batch")
            {
                byte[] body = await ReadBodyAsync(context.Request, 4096, canceled.Token).ConfigureAwait(false);
                requestDetails += $"; Selection={System.Text.Encoding.UTF8.GetString(body)}";
                var selection = JsonSerializer.Deserialize(body, PhoneCaptureJsonContext.Default.PhoneCaptureSelection);
                if (selection?.Ids is not { Length: > 0 and <= LibraryLimits.MaximumPages } ids || ids.Any(string.IsNullOrWhiteSpace) || ids.Distinct(StringComparer.Ordinal).Count() != ids.Length)
                    throw new InvalidDataException("Select between 1 and 12 distinct files");
                foreach (string id in ids)
                    if (!_uploads.TryGetValue(id, out var selected) || (!selected.Info.Pdf && selected.Processed is null))
                        throw new InvalidDataException("Every selected photo must have a corrected preview before batch confirmation");
                foreach (string id in ids) _uploads[id] = _uploads[id] with { Accepted = true };
                Publish();
            }
            else
            {
                string[] parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length != 2 || !_uploads.TryGetValue(parts[1], out var upload)) { context.Response.StatusCode = 404; return; }
                if (parts[0] == "image" && HttpMethods.IsGet(context.Request.Method) && upload.Preview is not null)
                {
                    context.Response.ContentType = "image/png";
                    await context.Response.Body.WriteAsync(upload.Preview, canceled.Token).ConfigureAwait(false);
                }
                else if (parts[0] == "result" && HttpMethods.IsGet(context.Request.Method) && upload.Processed is not null)
                {
                    context.Response.ContentType = "image/png";
                    await context.Response.Body.WriteAsync(upload.Processed, canceled.Token).ConfigureAwait(false);
                }
                else if (parts[0] == "render" && HttpMethods.IsPost(context.Request.Method) && !upload.Info.Pdf && !upload.Accepted)
                {
                    byte[] body = await ReadBodyAsync(context.Request, 4096, canceled.Token).ConfigureAwait(false);
                    requestDetails += $"; Correction={System.Text.Encoding.UTF8.GetString(body)}";
                    var correction = JsonSerializer.Deserialize(body, PhoneCaptureJsonContext.Default.PhotoCorrection) ?? throw new InvalidDataException("Missing photo corners");
                    if (correction.Backend == "standard" && correction.Processing is { Crop: true, Rectify: false } && (correction.Corners is null || correction.Corners.Length is not (0 or 4) || correction.Corners.Any(p => p is null || p.Length != 2))) throw new InvalidDataException("Use automatic cropping or select four photo corners");
                    if (correction.Backend is not ("standard" or "docres") || correction.Enhancement is not ("original" or "color" or "gray")) throw new InvalidDataException("Unknown photo restoration mode");
                    if (correction.Tasks is not null && (correction.Tasks.Any(task => task is not ("dewarping" or "deshadowing" or "deblurring" or "appearance")) || correction.Tasks.Distinct().Count() != correction.Tasks.Length)) throw new InvalidDataException("Invalid DocRes restoration tasks");
                    byte[] processed = await _process(upload.Original, correction, canceled.Token).ConfigureAwait(false);
                    if (processed.Length > LibraryLimits.MaximumAssetBytes || RetainedBytes - (upload.Processed?.LongLength ?? 0) + processed.LongLength > LibraryLimits.MaximumBatchBytes)
                        throw new InvalidDataException("Processed photos exceed the import limit");
                    _uploads[upload.Info.Id] = upload with { Processed = processed, Info = upload.Info with { Correction = correction } };
                    context.Response.ContentType = "image/png";
                    await context.Response.Body.WriteAsync(processed, canceled.Token).ConfigureAwait(false);
                }
                else if (parts[0] == "accept" && HttpMethods.IsPost(context.Request.Method) && (upload.Info.Pdf || upload.Processed is not null))
                { _uploads[upload.Info.Id] = upload with { Accepted = true }; Publish(); }
                else if (parts[0] == "remove" && HttpMethods.IsPost(context.Request.Method))
                { _uploads.Remove(upload.Info.Id); Publish(); }
                else context.Response.StatusCode = 404;
            }
        }
        catch (OperationCanceledException) when (canceled.IsCancellationRequested) { if (!context.Response.HasStarted) context.Response.StatusCode = 410; }
        catch (Exception error)
        {
            _logError(new IOException($"Phone capture request failed: {requestDetails}", error));
            if (!context.Response.HasStarted)
            {
                context.Response.StatusCode = error is PhotoRestorationException ? 422 : error is InvalidDataException or JsonException or BadHttpRequestException ? 400 : 500;
                context.Response.ContentType = "text/plain; charset=utf-8";
                await context.Response.WriteAsync("The file could not be received or processed. Check its format and size, then retry.", context.RequestAborted).ConfigureAwait(false);
            }
        }
        finally { if (entered) _gate.Release(); }
    }

    private long RetainedBytes => _uploads.Values.Sum(u => u.Original.LongLength + (u.Preview?.LongLength ?? 0) + (u.Processed?.LongLength ?? 0));

    private void Publish()
    {
        Volatile.Write(ref _uploadedCount, _uploads.Count);
        Volatile.Write(ref _accepted, _uploads.Values.Where(u => u.Accepted).OrderBy(u => u.Order).Select(u => u.Info with { Ready = true }).ToArray());
        Changed?.Invoke();
    }

    public async Task<IReadOnlyList<ImportSource>> TakeFilesAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { return _uploads.Values.Where(u => u.Accepted).OrderBy(u => u.Order).Select(u => new ImportSource(u.Info.Name, u.Original, ProcessedImagePng: u.Processed)).ToArray(); }
        finally { _gate.Release(); }
    }

    private static async Task<byte[]> ReadBodyAsync(HttpRequest request, int maximum, CancellationToken cancellationToken)
    {
        if (request.ContentLength is > 0 && request.ContentLength > maximum) throw new InvalidDataException("File exceeds the upload limit");
        using var output = new MemoryStream();
        var buffer = new byte[81920];
        int count;
        while ((count = await request.Body.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (output.Length + count > maximum) throw new InvalidDataException("File exceeds the upload limit");
            await output.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (output.Length == 0) throw new InvalidDataException("The upload is empty");
        return output.ToArray();
    }

    public async ValueTask DisposeAsync()
    {
        await _expiration.CancelAsync().ConfigureAwait(false);
        if (_app is not null)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            try { await _app.StopAsync(timeout.Token).ConfigureAwait(false); }
            finally { await _app.DisposeAsync().ConfigureAwait(false); }
            _app = null;
        }
        _uploads.Clear();
        _expiration.Dispose();
    }
}
