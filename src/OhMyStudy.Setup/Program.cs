using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyStudy.Setup;

internal static partial class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        bool quiet = args.Contains("--quiet", StringComparer.Ordinal);
        string? report = Value(args, "--report");
        string ownDirectory = Path.GetDirectoryName(Environment.ProcessPath)!;
        string root = Value(args, "--root") ?? (File.Exists(Path.Combine(ownDirectory, "installation.json")) ? ownDirectory : InstallerEngine.DefaultRoot);
        string operation = args.Contains("--uninstall", StringComparer.Ordinal) ? "uninstall"
            : args.Contains("--launch", StringComparer.Ordinal) || args.Length == 0 && File.Exists(Path.Combine(ownDirectory, "active.json")) ? "launch" : "install";
        try
        {
            if (!OperatingSystem.IsWindows() || !Environment.Is64BitProcess) throw new PlatformNotSupportedException("Run this app on a 64-bit Windows computer.");
            if (Value(args, "--wait-pid") is { } pidText && int.TryParse(pidText, out int pid))
                try { using var parent = Process.GetProcessById(pid); if (!parent.WaitForExit(30_000)) throw new IOException("Close the app and retry."); } catch (ArgumentException) { }
            if (operation == "uninstall" && SafePaths.Within(Environment.ProcessPath!, SafePaths.Root(root)))
            {
                if (!quiet && MessageBox(0, "Uninstall Oh My Study? Your materials will be kept.", "Oh My Study", 0x24) != 6) return 0;
                // A running executable cannot remove itself. The copied native helper waits for this exact caller to exit.
                var helper = Directory.CreateTempSubdirectory("OhMyStudy-Uninstall-");
                string copy = Path.Combine(helper.FullName, InstallerEngine.BootstrapName);
                File.Copy(Environment.ProcessPath!, copy, overwrite: false);
                var start = new ProcessStartInfo(copy) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = helper.FullName };
                foreach (string value in new[] { "--uninstall", "--root", root, "--confirmed", "--wait-pid", Environment.ProcessId.ToString(System.Globalization.CultureInfo.InvariantCulture) }) start.ArgumentList.Add(value);
                if (quiet) start.ArgumentList.Add("--quiet");
                if (report is not null) { start.ArgumentList.Add("--report"); start.ArgumentList.Add(report); }
                using var process = Process.Start(start) ?? throw new IOException("Cannot start uninstall.");
                return 0;
            }
            var engine = new InstallerEngine();
            InstallResult result;
            if (operation == "launch")
            {
                string application = engine.ResolveLaunch(root);
                using var process = Process.Start(new ProcessStartInfo(application) { UseShellExecute = false, WorkingDirectory = Path.GetDirectoryName(application)! });
                if (process is null) throw new IOException("The app could not be opened.");
                result = new(root, null, false);
            }
            else if (operation == "uninstall")
            {
                if (!quiet && !args.Contains("--confirmed", StringComparer.Ordinal) && MessageBox(0, "Uninstall Oh My Study? Your materials will be kept.", "Oh My Study", 0x24) != 6) return 0;
                result = engine.Uninstall(root);
                if (!quiet) MessageBox(0, "Uninstalled. Your materials have been kept.", "Oh My Study", 0x40);
            }
            else
            {
                if (!quiet && MessageBox(0, "Install Oh My Study? You can open it from the Start menu after installation.", "Oh My Study", 0x24) != 6) return 0;
                string? source = Value(args, "--source");
                using var payload = source is null ? BundledPayload.Open() : null;
                result = engine.Install(new(source ?? payload?.DirectoryPath ?? ownDirectory, root, !args.Contains("--no-shortcuts", StringComparer.Ordinal)));
                if (!quiet) MessageBox(0, "Installation complete. Open Oh My Study from the Start menu.", "Oh My Study", 0x40);
            }
            WriteReport(report, new(true, operation, result, !RuntimeFeature.IsDynamicCodeSupported, null));
            return 0;
        }
        catch (Exception error)
        {
            string message = error is InvalidDataException or PlatformNotSupportedException ? error.Message : "The operation did not complete. Close the app, check the installation files and folders, and retry.";
            WriteReport(report, new(false, operation, null, !RuntimeFeature.IsDynamicCodeSupported, message, error.ToString()));
            if (!quiet) MessageBox(0, message, "Oh My Study", 0x10);
            return 1;
        }
    }
    private static string? Value(string[] args, string name)
    {
        int index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }
    private static void WriteReport(string? path, SetupReport report)
    {
        if (path is null) return;
        string full = Path.GetFullPath(path); SafePaths.AssertUnlinked(full);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        InstallerEngine.AtomicWrite(full, JsonSerializer.SerializeToUtf8Bytes(report, ReportJson.Default.SetupReport));
    }
    [LibraryImport("user32.dll", EntryPoint = "MessageBoxW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBox(nint owner, string text, string title, uint flags);
}

internal sealed record SetupReport(bool Success, string Operation, InstallResult? Result, bool NativeAot, string? Error, string? Diagnostic = null);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(SetupReport))]
internal partial class ReportJson : JsonSerializerContext;
