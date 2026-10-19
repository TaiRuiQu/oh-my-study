using System.Diagnostics;
using Microsoft.UI.Dispatching;
using OhMyStudy.App.Diagnostics;

namespace OhMyStudy.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        AppLog.Info("Process.Start", $"PID={Environment.ProcessId}; Thread={Environment.CurrentManagedThreadId}; Apartment={Thread.CurrentThread.GetApartmentState()}; Log={AppLog.FilePath ?? "console"}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error) AppLog.Error("AppDomain.UnhandledException", error);
        };
        TaskScheduler.UnobservedTaskException += (_, e) => AppLog.Error("Task.UnobservedException", e.Exception);
        try
        {
#if DEBUG
            if (!Debugger.IsAttached && !args.Contains("--ui-process", StringComparer.Ordinal)) return RunDebugHost(args);
#endif
            WinRT.ComWrappersSupport.InitializeComWrappers();
            Microsoft.UI.Xaml.Application.Start(parameters =>
            {
                SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(DispatcherQueue.GetForCurrentThread()));
                AppLog.Info("Application.Create", "Creating the application.");
                _ = new App();
            });
            AppLog.Info("Application.Exit", "The UI event loop ended.");
            return 0;
        }
        catch (Exception error)
        {
            AppLog.Error("Process.UnhandledException", error);
            return 1;
        }
    }

#if DEBUG
    // A native WinUI fail-fast never reaches C# exception handlers. Keep a console
    // process outside the UI process so even those failures have a visible exit code.
    private static int RunDebugHost(string[] args)
    {
        var start = new ProcessStartInfo(Environment.ProcessPath ?? throw new IOException("Application executable path is unavailable."))
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        if (Path.GetFileNameWithoutExtension(start.FileName).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "OhMyStudy.App.dll"));
        start.ArgumentList.Add("--ui-process");
        foreach (string argument in args) start.ArgumentList.Add(argument);
        if (AppLog.FilePath is { } log) start.Environment["OHMYSTUDY_LOG_FILE"] = log;
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) Console.Out.WriteLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) Console.Error.WriteLine(e.Data); };
        process.Start();
        AppLog.Info("Process.UiStarted", $"PID={process.Id}");
        process.BeginOutputReadLine(); process.BeginErrorReadLine();
        int stopRequested = 0;
        void StopUiProcess()
        {
            try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
            catch (InvalidOperationException) { }
            catch (Exception error) { AppLog.Error("Process.StopUiFailed", error); }
        }
        ConsoleCancelEventHandler cancel = (_, e) =>
        {
            e.Cancel = true;
            Interlocked.Exchange(ref stopRequested, 1);
            StopUiProcess();
        };
        EventHandler exit = (_, _) => StopUiProcess();
        Console.CancelKeyPress += cancel;
        AppDomain.CurrentDomain.ProcessExit += exit;
        try { process.WaitForExit(); }
        finally
        {
            Console.CancelKeyPress -= cancel;
            AppDomain.CurrentDomain.ProcessExit -= exit;
        }
        int code = process.ExitCode;
        if (Volatile.Read(ref stopRequested) != 0) AppLog.Info("Process.UiExited", $"Stopped from console. ExitCode=0x{unchecked((uint)code):X8}");
        else if (code == 0) AppLog.Info("Process.UiExited", "ExitCode=0");
        else AppLog.Failure("Process.UiExited", $"UI process exited unexpectedly. ExitCode=0x{unchecked((uint)code):X8} ({code}). Native failures bypass C# exception handlers. Log={AppLog.FilePath}");
        return code;
    }
#endif
}
