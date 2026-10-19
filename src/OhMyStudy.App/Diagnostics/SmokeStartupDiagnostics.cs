using System.Collections;
using System.Text;

namespace OhMyStudy.App.Diagnostics;

internal static class SmokeStartupDiagnostics
{
    /// <summary>Only explicit smoke runs persist startup exception details; a crash never becomes a passing report.</summary>
    public static void Log(string phase, Exception exception)
    {
        AppLog.Error(phase, exception);
        try
        {
            if (ReportPath() is null) return;
            // Capture restricted WinRT descriptions before another interop call replaces the thread error info.
            WinRT.ExceptionHelpers.AttachRestrictedErrorInfo(exception);
            var text = new StringBuilder();
            for (Exception? current = exception; current is not null; current = current.InnerException)
            {
                text.AppendLine($"Type: {current.GetType().FullName}\nHResult: 0x{current.HResult:X8}\n{current}");
                foreach (DictionaryEntry item in current.Data)
                    text.AppendLine($"Data[{item.Key}] = {item.Value}");
                if (current.InnerException is not null) text.AppendLine("Inner exception:");
            }
            LogText(phase, text.ToString());
        }
        catch { /* Startup diagnostic I/O must not replace the original exception. */ }
    }

    public static void LogText(string phase, string message)
    {
        AppLog.Info(phase, message);
        try
        {
            string? report = ReportPath();
            if (report is null) return;
            string path = Path.ChangeExtension(report, ".startup-error.txt");
            string? directory = Path.GetDirectoryName(path);
            if (directory is not null) Directory.CreateDirectory(directory);
            File.AppendAllText(path, $"{DateTimeOffset.UtcNow:O} {phase}\n{message}\n\n");
        }
        catch { }
    }

    private static string? ReportPath()
    {
        string[] arguments = Environment.GetCommandLineArgs();
        int index = Array.IndexOf(arguments, "--smoke-test");
        return index >= 0 && index + 1 < arguments.Length ? Path.GetFullPath(arguments[index + 1]) : null;
    }
}
