using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyStudy.Documents;

public sealed record DocResStatus(bool Eligible, bool Installed, bool Enabled, string Reason);
internal sealed record DocResRequest(string Input, string Output, int Width, int Height, int DeviceIndex, string[] Tasks, bool PreCropped);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(DocResRequest))]
internal partial class DocResJsonContext : JsonSerializerContext;

public static class DocResRestoration
{
    public static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OhMyStudy", "DocRes");
    private static string RuntimeLibrary => Path.Combine(Root, "python", "Lib", "site-packages", "torch", "lib");
    private static string Python => Path.Combine(Root, "python", "python.exe");
    private static string Scripts => Path.Combine(AppContext.BaseDirectory, "native", "docres");

    public static DocResStatus Status()
    {
        var gpu = DocumentRestorationGpu.Probe(RuntimeLibrary);
        bool installed = File.Exists(Path.Combine(Root, "verified.json")) && File.Exists(Python) && File.Exists(Path.Combine(Root, "docres.pkl")) && File.Exists(Path.Combine(Root, "mbd.pkl"));
        return new(gpu.Device is not null, installed, installed && File.Exists(Path.Combine(Root, "enabled")), gpu.Reason);
    }

    public static void SetEnabled(bool enabled)
    {
        string path = Path.Combine(Root, "enabled");
        if (enabled)
        {
            var status = Status();
            if (!status.Eligible || !status.Installed) throw new InvalidOperationException("Install DocRes on an eligible CUDA device before enabling it. " + status.Reason);
            File.WriteAllText(path, "enabled");
        }
        else if (File.Exists(path)) File.Delete(path);
    }

    public static async Task InstallAsync(Action<string> progress, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var gpu = DocumentRestorationGpu.Probe(RuntimeLibrary);
        if (gpu.Device is null) throw new InvalidOperationException(gpu.Reason);
        string bootstrap = Path.Combine(AppContext.BaseDirectory, "runtime", "python", "python.exe");
        if (!File.Exists(bootstrap)) throw new FileNotFoundException("The private Python bootstrap is missing. Reinstall the complete application package.", bootstrap);
        await RunAsync(bootstrap, ["-I", "-B", "-X", "utf8", Path.Combine(Scripts, "install.py"), "--destination", Root, "--device", gpu.Device.Index.ToString(System.Globalization.CultureInfo.InvariantCulture)], progress, cancellationToken).ConfigureAwait(false);
        if (!Status().Installed) throw new InvalidDataException("DocRes installation did not pass its CUDA self-test.");
    }

    public static async Task<ScanRaster> RestoreAsync(ScanRaster raster, string[] tasks, CancellationToken cancellationToken, Action<string>? progress = null, bool preCropped = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tasks.Length == 0 || tasks.Any(t => t is not ("dewarping" or "deshadowing" or "deblurring" or "appearance")) || tasks.Distinct().Count() != tasks.Length)
            throw new InvalidDataException("Select valid DocRes restoration tasks");
        var status = Status();
        if (!status.Eligible || !status.Installed || !status.Enabled) throw new InvalidOperationException("DocRes is unavailable. Select standard restoration, or install and enable DocRes on this computer. " + status.Reason);
        var gpu = DocumentRestorationGpu.Probe(RuntimeLibrary).Device ?? throw new InvalidOperationException("The selected CUDA device is no longer available.");
        string temporary = Path.Combine(Root, "work", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temporary);
        try
        {
            string input = Path.Combine(temporary, "input.bgra"), output = Path.Combine(temporary, "output.bgra"), request = Path.Combine(temporary, "request.json");
            await File.WriteAllBytesAsync(input, raster.Bgra32, cancellationToken).ConfigureAwait(false);
            var data = new DocResRequest(input, output, raster.Width, raster.Height, gpu.Index, tasks, preCropped);
            await File.WriteAllTextAsync(request, JsonSerializer.Serialize(data, DocResJsonContext.Default.DocResRequest), cancellationToken).ConfigureAwait(false);
            await RunAsync(Python, ["-I", "-B", "-X", "utf8", Path.Combine(Scripts, "worker.py"), "--root", Root, "--request", request], progress, cancellationToken).ConfigureAwait(false);
            byte[] bytes = await File.ReadAllBytesAsync(output, cancellationToken).ConfigureAwait(false);
            if (bytes.LongLength != (long)raster.Width * raster.Height * 4) throw new InvalidDataException("DocRes returned invalid image dimensions");
            return new(bytes, raster.Width, raster.Height);
        }
        finally { Directory.Delete(temporary, true); }
    }

    private static async Task RunAsync(string executable, string[] arguments, Action<string>? progress, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in arguments) start.ArgumentList.Add(argument);
        start.Environment.Remove("CUDA_VISIBLE_DEVICES");
        start.Environment.Remove("CUDA_DEVICE_ORDER");
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start the DocRes worker");
        using var registration = cancellationToken.Register(() => { try { if (!process.HasExited) process.Kill(true); } catch (InvalidOperationException) { } });
        var stderr = process.StandardError.ReadToEndAsync(cancellationToken);
        var stdout = new System.Text.StringBuilder();
        try
        {
            while (await process.StandardOutput.ReadLineAsync(cancellationToken).ConfigureAwait(false) is string line)
            {
                stdout.AppendLine(line);
                progress?.Invoke(line);
            }
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (!process.HasExited) process.Kill(true);
            await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
        string errors = await stderr.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (process.ExitCode != 0) throw new InvalidOperationException($"DocRes worker failed (exit {process.ExitCode}). Use standard restoration or retry with fewer tasks. CUDA memory exhaustion may require closing GPU applications. Arguments: {string.Join(" ", arguments)}{Environment.NewLine}{stdout}{Environment.NewLine}{errors}");
    }
}
