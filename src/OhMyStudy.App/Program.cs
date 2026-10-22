using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;
using Microsoft.Win32.SafeHandles;
using OhMyStudy.App.Diagnostics;

namespace OhMyStudy.App;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
#if DEBUG
        // The app is a WinExe in every configuration, so a double-click never creates a
        // console window. When it is launched from a terminal instead, attach to that
        // terminal so developer logs and the debug-host exit code remain visible. The UI
        // child keeps its redirected pipes; only the host attaches.
        if (!args.Contains("--ui-process", StringComparer.Ordinal)) AttachParentConsole();
#endif
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
                try { _ = new App(); }
                catch (Exception error) { AppLog.Error("Application.Create", error); throw; }
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
    private const int AttachParentProcess = -1;
    private const uint GenericRead = 0x80000000, GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x1, FileShareWrite = 0x2, OpenExisting = 3;

    private static void AttachParentConsole()
    {
        try
        {
            if (!NativeAttachConsole(AttachParentProcess)) return;
            nint handle = NativeCreateFile("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, 0, OpenExisting, 0, 0);
            if (handle == 0 || handle == -1) return;
            var writer = new StreamWriter(new FileStream(new SafeFileHandle(handle, ownsHandle: true), FileAccess.Write)) { AutoFlush = true };
            Console.SetOut(writer);
            Console.SetError(writer);
        }
        catch
        {
            // A missing or redirected console must never prevent the app from starting.
        }
    }

    [DllImport("kernel32.dll", EntryPoint = "AttachConsole", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] private static extern bool NativeAttachConsole(int processId);
    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)] private static extern nint NativeCreateFile(string name, uint access, uint share, nint security, uint creation, uint flags, nint template);

    // A native WinUI fail-fast never reaches C# exception handlers. Keep a separate
    // host process outside the UI process so those failures still produce a non-zero
    // exit code and file log; a terminal-attached host also shows the log.
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
