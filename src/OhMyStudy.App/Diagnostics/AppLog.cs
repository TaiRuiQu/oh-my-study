using System.Diagnostics;

namespace OhMyStudy.App.Diagnostics;

/// <summary>Local developer diagnostics. Never log credentials, request bodies or document contents.</summary>
internal static class AppLog
{
    private static readonly object Gate = new();
    public static string? FilePath { get; }
    static AppLog()
    {
        try { Console.OutputEncoding = new System.Text.UTF8Encoding(false); }
        catch { }
        try
        {
            FilePath = Environment.GetEnvironmentVariable("OHMYSTUDY_LOG_FILE");
#if DEBUG
            FilePath ??= Path.Combine(AppContext.BaseDirectory, "logs", $"debug-{DateTime.Now:yyyyMMdd-HHmmss}-{Environment.ProcessId}.log");
#endif
            if (FilePath is not null) Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(FilePath))!);
        }
        catch { FilePath = null; }
    }

    public static void Info(string phase, string message) => Write("INFO", phase, message);
    public static void Error(string phase, Exception exception) => Write("ERROR", phase, exception.ToString());
    public static void Failure(string phase, string message) => Write("ERROR", phase, message);

    private static void Write(string level, string phase, string message)
    {
        // A disconnected console must never replace the operation's original outcome.
        try
        {
            string line = $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz} [{level}] {phase}: {message}";
            lock (Gate)
            {
                try { Console.Error.WriteLine(line); Console.Error.Flush(); }
                catch { }
                try { Debug.WriteLine(line); }
                catch { }
                try { if (FilePath is not null) File.AppendAllText(FilePath, line + Environment.NewLine, System.Text.Encoding.UTF8); }
                catch { }
            }
        }
        catch { }
    }
}
