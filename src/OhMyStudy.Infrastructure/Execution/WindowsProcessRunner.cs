using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
using OhMyStudy.Application.Jobs;

namespace OhMyStudy.Infrastructure.Execution;

public sealed record ProcessRequest(string Executable, IReadOnlyList<string> Arguments, string WorkingDirectory,
    IReadOnlyDictionary<string, string> Environment, TimeSpan Timeout, int OutputLimit = 1_048_576, AppContainerProfile? AppContainer = null);

/// <summary>Starts suspended, owns the process tree before resume, and inherits only three standard handles.</summary>
public sealed partial class WindowsProcessRunner
{
    public async Task<ToolExecutionResult> RunAsync(ProcessRequest request, CancellationToken cancellationToken = default)
    {
        if (request.Timeout <= TimeSpan.Zero || request.Timeout > TimeSpan.FromHours(1)) throw new ArgumentOutOfRangeException(nameof(request));
        if (request.OutputLimit is <= 0 or > 64 * 1024 * 1024) throw new ArgumentOutOfRangeException(nameof(request));
        cancellationToken.ThrowIfCancellationRequested();
        var executable = RuntimePaths.CheckedFile(request.Executable);
        var cwd = RuntimePaths.CheckedDirectory(request.WorkingDirectory);
        var commandLine = string.Join(' ', new[] { executable }.Concat(request.Arguments).Select(QuoteArgument));
        if (commandLine.Length > 32766 || commandLine.Contains('\0')) throw new ArgumentException("命令行过长或含无效字符。");
        var processEnvironment = new Dictionary<string, string>(request.Environment, StringComparer.OrdinalIgnoreCase);
        if (request.AppContainer is not null)
        {
            processEnvironment["USERPROFILE"] = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            processEnvironment["LOCALAPPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            processEnvironment["APPDATA"] = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        }
        var environment = EnvironmentBlock(processEnvironment);

        using var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable);
        using var nul = Native.CreateFileW("NUL", 0x80000000, 3, 0, 3, 0, 0);
        if (nul.IsInvalid) throw LastError();
        if (!Native.DuplicateHandle(Native.GetCurrentProcess(), nul.DangerousGetHandle(), Native.GetCurrentProcess(), out var stdin, 0, true, 2)) throw LastError();
        using (stdin)
        using (var job = Native.CreateJobObjectW(0, null))
        {
            if (job.IsInvalid) throw LastError();
            var limits = new Native.JobLimits { Basic = new() { LimitFlags = 0x2000 } };
            if (request.AppContainer is not null)
            {
                limits.Basic.LimitFlags |= 0x200;
                limits.JobMemory = 2_147_483_648;
            }
            if (!Native.SetInformationJobObject(job, 9, in limits, (uint)Marshal.SizeOf<Native.JobLimits>())) throw LastError();
            using var attributes = new StartupAttributes([stdin.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle()], request.AppContainer);
            var startup = new Native.StartupInfoEx
            {
                Info = new()
                {
                    Size = (uint)Marshal.SizeOf<Native.StartupInfoEx>(),
                    Flags = 0x100,
                    Stdin = stdin.DangerousGetHandle(),
                    Stdout = stdout.ClientSafePipeHandle.DangerousGetHandle(),
                    Stderr = stderr.ClientSafePipeHandle.DangerousGetHandle()
                },
                Attributes = attributes.Pointer
            };
            var writableCommand = (commandLine + '\0').ToCharArray();
            Native.ProcessInfo created;
            bool started;
            unsafe
            {
                fixed (char* command = writableCommand)
                fixed (char* variables = environment)
                    started = Native.CreateProcessW(executable, command, 0, 0, true, 0x08000000 | 0x4 | 0x400 | 0x80000,
                        variables, cwd, in startup, out created);
            }
            if (!started) throw LastError();
            using var process = new SafeFileHandle(created.Process, true);
            using var thread = new SafeFileHandle(created.Thread, true);
            // Any failure before assignment must explicitly terminate the still suspended process.
            if (!Native.AssignProcessToJobObject(job, process))
            {
                var error = LastError();
                Native.TerminateProcess(process, 1);
                Native.WaitForSingleObject(process, 5000);
                throw error;
            }
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();
            using var outputExceeded = new CancellationTokenSource();
            var capture = new OutputCapture(request.OutputLimit, outputExceeded);
            var outputTask = capture.ReadAsync(stdout);
            var errorTask = capture.ReadAsync(stderr);
            string status = "completed";
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (Native.ResumeThread(thread) == uint.MaxValue) throw LastError();
                var clock = Stopwatch.StartNew();
                while (true)
                {
                    if (cancellationToken.IsCancellationRequested) { status = "cancelled"; break; }
                    if (outputExceeded.IsCancellationRequested) { status = "output_limit"; break; }
                    if (clock.Elapsed >= request.Timeout) { status = "timed_out"; break; }
                    uint wait = Native.WaitForSingleObject(process, 0);
                    if (wait == 0) break;
                    if (wait != 258) throw LastError();
                    await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
                }
            }
            finally
            {
                // Includes descendants left behind when a root exits normally.
                job.Dispose();
                if (Native.WaitForSingleObject(process, 5000) != 0)
                    throw new IOException("执行进程未能退出。");
            }
            var logs = await Task.WhenAll(outputTask, errorTask).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            if (!Native.GetExitCodeProcess(process, out var exitCode)) throw LastError();
            if (status == "completed" && outputExceeded.IsCancellationRequested) status = "output_limit";
            if (status == "completed" && exitCode != 0) status = "failed";
            return new(status, exitCode, logs[0], logs[1]);
        }
    }

    public static string QuoteArgument(string value)
    {
        if (value.Contains('\0')) throw new ArgumentException("参数含无效字符。");
        var result = new StringBuilder("\"");
        int slashes = 0;
        foreach (char c in value)
        {
            if (c == '\\') { slashes++; continue; }
            result.Append('\\', c == '"' ? slashes * 2 + 1 : slashes); slashes = 0;
            result.Append(c);
        }
        return result.Append('\\', slashes * 2).Append('"').ToString();
    }

    public static IReadOnlyDictionary<string, string> CreateEnvironment(string runtimeDirectory, string temporaryDirectory)
    {
        string system = System.Environment.GetFolderPath(System.Environment.SpecialFolder.Windows);
        return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SystemRoot"] = system,
            ["WINDIR"] = system,
            ["TEMP"] = temporaryDirectory,
            ["TMP"] = temporaryDirectory,
            ["PATH"] = runtimeDirectory + ";" + Path.Combine(system, "System32"),
            ["MPLBACKEND"] = "Agg",
            ["MPLCONFIGDIR"] = Path.Combine(temporaryDirectory, "matplotlib"),
            ["OMP_NUM_THREADS"] = "2",
            ["OPENBLAS_NUM_THREADS"] = "2",
            ["PYTHONIOENCODING"] = "utf-8"
        };
    }

    private static char[] EnvironmentBlock(IReadOnlyDictionary<string, string> variables)
    {
        if (variables.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Contains('=') || p.Key.Contains('\0') || p.Value.Contains('\0')))
            throw new ArgumentException("无效的环境变量。");
        return (string.Join('\0', variables.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase).Select(p => p.Key + "=" + p.Value)) + "\0\0").ToCharArray();
    }

    private static Win32Exception LastError() => new(Marshal.GetLastPInvokeError());

    private sealed class OutputCapture(int limit, CancellationTokenSource exceeded)
    {
        private long _total;
        public async Task<string> ReadAsync(Stream stream)
        {
            using var saved = new MemoryStream();
            byte[] buffer = new byte[8192];
            while (true)
            {
                int count = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (count == 0) break;
                long total = Interlocked.Add(ref _total, count);
                int allowed = (int)Math.Clamp(limit - (total - count), 0, count);
                if (allowed > 0) saved.Write(buffer, 0, allowed);
                if (total > limit) exceeded.Cancel();
            }
            var text = Encoding.UTF8.GetString(saved.GetBuffer(), 0, (int)saved.Length);
            int bytes = 0, characters = 0;
            foreach (var rune in text.EnumerateRunes())
            {
                if (bytes + rune.Utf8SequenceLength > saved.Length) break;
                bytes += rune.Utf8SequenceLength;
                characters += rune.Utf16SequenceLength;
            }
            return text[..characters];
        }
    }

    private sealed unsafe class StartupAttributes : IDisposable
    {
        public nint Pointer { get; private set; }
        private nint _handles;
        private nint _security;
        private nint _capability;
        private nint _networkSid;
        private bool _initialized;
        public StartupAttributes(nint[] handles, AppContainerProfile? container)
        {
            int count = container is null ? 1 : 2;
            nuint size = 0;
            Native.InitializeProcThreadAttributeList(0, count, 0, ref size);
            Pointer = Marshal.AllocHGlobal(checked((int)size));
            try
            {
                if (!Native.InitializeProcThreadAttributeList(Pointer, count, 0, ref size)) throw LastError();
                _initialized = true;
                _handles = Marshal.AllocHGlobal(handles.Length * IntPtr.Size);
                Marshal.Copy(handles, 0, _handles, handles.Length);
                if (!Native.UpdateProcThreadAttribute(Pointer, 0, 0x20002, _handles, (nuint)(handles.Length * IntPtr.Size), 0, 0)) throw LastError();
                if (container is not null)
                {
                    if (container.Networking)
                    {
                        if (!Native.ConvertStringSidToSidW("S-1-15-3-1", out _networkSid)) throw LastError();
                        _capability = Marshal.AllocHGlobal(sizeof(Native.SidAndAttributes));
                        *(Native.SidAndAttributes*)_capability = new() { Sid = _networkSid, Attributes = 4 };
                    }
                    _security = Marshal.AllocHGlobal(sizeof(Native.SecurityCapabilities));
                    *(Native.SecurityCapabilities*)_security = new() { AppContainerSid = container.Sid, Capabilities = _capability, Count = container.Networking ? 1u : 0 };
                    if (!Native.UpdateProcThreadAttribute(Pointer, 0, 0x20009, _security, (nuint)sizeof(Native.SecurityCapabilities), 0, 0)) throw LastError();
                }
            }
            catch { Dispose(); throw; }
        }
        public void Dispose()
        {
            if (Pointer != 0) { if (_initialized) Native.DeleteProcThreadAttributeList(Pointer); Marshal.FreeHGlobal(Pointer); Pointer = 0; }
            if (_handles != 0) { Marshal.FreeHGlobal(_handles); _handles = 0; }
            if (_security != 0) { Marshal.FreeHGlobal(_security); _security = 0; }
            if (_capability != 0) { Marshal.FreeHGlobal(_capability); _capability = 0; }
            if (_networkSid != 0) { Native.LocalFree(_networkSid); _networkSid = 0; }
        }
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        internal struct BasicLimits
        { public long ProcessTime, JobTime; public uint LimitFlags; public nuint MinimumWorkingSet, MaximumWorkingSet; public uint ActiveProcessLimit; public nuint Affinity; public uint Priority, Scheduling; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct JobLimits
        { public BasicLimits Basic; public ulong ReadOps, WriteOps, OtherOps, ReadBytes, WriteBytes, OtherBytes; public nuint ProcessMemory, JobMemory, PeakProcess, PeakJob; }
        [StructLayout(LayoutKind.Sequential)]
        internal struct StartupInfo
        { public uint Size; public nint Reserved, Desktop, Title; public uint X, Y, XSize, YSize, XChars, YChars, Fill, Flags; public ushort Show, ReservedSize; public nint ReservedBytes, Stdin, Stdout, Stderr; }
        [StructLayout(LayoutKind.Sequential)] internal struct StartupInfoEx { public StartupInfo Info; public nint Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct ProcessInfo { public nint Process, Thread; public uint ProcessId, ThreadId; }
        [StructLayout(LayoutKind.Sequential)] internal struct SidAndAttributes { public nint Sid; public uint Attributes; }
        [StructLayout(LayoutKind.Sequential)] internal struct SecurityCapabilities { public nint AppContainerSid, Capabilities; public uint Count, Reserved; }

        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool ConvertStringSidToSidW(string text, out nint sid);
        [LibraryImport("kernel32.dll")] internal static partial nint LocalFree(nint memory);

        [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] internal static partial SafeFileHandle CreateJobObjectW(nint attributes, string? name);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool SetInformationJobObject(SafeFileHandle job, int infoClass, in JobLimits limits, uint length);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle process);
        [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static unsafe partial bool CreateProcessW(string application, char* command, nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, char* environment, string directory, in StartupInfoEx startup, out ProcessInfo process);
        [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial uint ResumeThread(SafeFileHandle thread);
        [LibraryImport("kernel32.dll", SetLastError = true)] internal static partial uint WaitForSingleObject(SafeFileHandle handle, uint milliseconds);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool GetExitCodeProcess(SafeFileHandle process, out uint exitCode);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool TerminateProcess(SafeFileHandle process, uint exitCode);
        [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)] internal static partial SafeFileHandle CreateFileW(string name, uint access, uint share, nint attributes, uint disposition, uint flags, nint template);
        [LibraryImport("kernel32.dll")] internal static partial nint GetCurrentProcess();
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool DuplicateHandle(nint sourceProcess, nint source, nint targetProcess, out SafeFileHandle target, uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint options);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool InitializeProcThreadAttributeList(nint attributes, int count, int flags, ref nuint size);
        [LibraryImport("kernel32.dll", SetLastError = true)][return: MarshalAs(UnmanagedType.Bool)] internal static partial bool UpdateProcThreadAttribute(nint attributes, uint flags, nuint key, nint value, nuint size, nint previous, nint returned);
        [LibraryImport("kernel32.dll")] internal static partial void DeleteProcThreadAttributeList(nint attributes);
    }
}
