using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using OhMyStudy.Application.Execution;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Execution;

internal static partial class AppContainerChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check, Action<bool, string> require)
    {
        var runtime = Path.Combine(root, "container-runtime"); Directory.CreateDirectory(runtime);
        var executable = Path.Combine(runtime, "probe.exe"); File.Copy(Environment.ProcessPath!, executable);
        var inputs = Path.Combine(root, "container-inputs"); Directory.CreateDirectory(inputs);
        var input = Path.Combine(inputs, "资料.txt"); await File.WriteAllTextAsync(input, "合成输入");
        var secret = Path.Combine(root, "container-private.txt"); await File.WriteAllTextAsync(secret, "must-not-read");
        var immutable = Path.Combine(runtime, "immutable.txt"); await File.WriteAllTextAsync(immutable, "read-only");
        foreach (bool network in new[] { false, true })
        {
            await check("appcontainer-token-files-environment-and-network-" + network, async () =>
            {
                var results = Path.Combine(root, "container-results-" + network); Directory.CreateDirectory(results);
                var before = Access(runtime);
                var id = Guid.NewGuid();
                using (var profile = AppContainerProfile.Create(id, network))
                {
                    try { using var duplicate = AppContainerProfile.Create(id); throw new Exception("existing profile adopted"); }
                    catch (Win32Exception error) when (error.NativeErrorCode == 183) { }
                    profile.GrantDirectory(runtime); profile.GrantDirectory(inputs); profile.GrantDirectory(results, true);
                    Environment.SetEnvironmentVariable("OMS_SYNTHETIC_SECRET", "must-not-inherit");
                    try
                    {
                        var request = new ProcessRequest(executable, ["--child", "appcontainer", secret, immutable, input, results, network.ToString()],
                            results, WindowsProcessRunner.CreateEnvironment(runtime, results), TimeSpan.FromSeconds(20), AppContainer: profile);
                        var result = await new WindowsProcessRunner().RunAsync(request);
                        require(result.Succeeded && result.Stdout.Contains("isolated"), $"status={result.Status}; exit={result.ExitCode}; {result.Stdout}{result.Stderr}");
                        result = await new WindowsProcessRunner().RunAsync(request with
                        {
                            Arguments = ["--child", "appcontainer-descendant", secret, immutable, input, results, network.ToString()]
                        });
                        require(result.Succeeded && result.Stdout.Contains("isolated"), $"descendant token: {result.Stdout}{result.Stderr}");
                        require(File.ReadAllText(Path.Combine(results, "artifact.txt")) == "合成输入", "result write failed");
                    }
                    finally { Environment.SetEnvironmentVariable("OMS_SYNTHETIC_SECRET", null); }
                }
                require(Access(runtime) == before && File.ReadAllText(immutable) == "read-only" && File.ReadAllText(input) == "合成输入", "permissions or inputs changed");
            });
        }
        await check("appcontainer-timeout-cancellation-and-descendant-cleanup", async () =>
        {
            var results = Path.Combine(root, "container-lifetime"); Directory.CreateDirectory(results);
            using var profile = AppContainerProfile.Create(Guid.NewGuid());
            profile.GrantDirectory(runtime); profile.GrantDirectory(results, true);
            ProcessRequest Request(string mode, params string[] arguments) => new(executable, new[] { "--child", mode }.Concat(arguments).ToArray(),
                results, WindowsProcessRunner.CreateEnvironment(runtime, results), TimeSpan.FromSeconds(5), AppContainer: profile);
            var runner = new WindowsProcessRunner();
            var result = await runner.RunAsync(Request("sleep") with { Timeout = TimeSpan.FromMilliseconds(150) });
            require(result.Status == "timed_out", "container timeout");
            using var cancel = new CancellationTokenSource(150);
            result = await runner.RunAsync(Request("sleep"), cancel.Token);
            require(result.Status == "cancelled", "container cancellation");
            var pidFile = Path.Combine(results, "descendant.pid");
            result = await runner.RunAsync(Request("spawn", pidFile));
            require(result.Succeeded && File.Exists(pidFile), $"container descendant launch: {result.Stderr}");
            int pid = int.Parse(await File.ReadAllTextAsync(pidFile));
            bool alive;
            try { using var process = System.Diagnostics.Process.GetProcessById(pid); alive = !process.HasExited; }
            catch (ArgumentException) { alive = false; }
            require(!alive, "container descendant survived");
        });
    }

    public static async Task RunWorkerAsync(string runtime, string root, Func<string, Func<Task>, Task> check, Action<bool, string> require)
    {
        await check("appcontainer-private-python-and-powershell", async () =>
        {
            var input = Path.Combine(root, "container-worker-input"); Directory.CreateDirectory(input);
            await File.WriteAllTextAsync(Path.Combine(input, "资料.txt"), "合成输入");
            var capability = await SandboxCapability.ProbeAsync(runtime);
            require(capability.State == CapabilityState.Ready, capability.Reason);
            using var broker = new PermissionBroker();
            broker.RegisterJob("container-worker", ExecutionPolicy.Freeze(ExecutionMode.Sandbox, capability, false));
            var executor = new SandboxToolExecutor(runtime);
            var call = new ExecutionCall
            {
                ProtocolVersion = 1,
                JobId = "container-worker",
                CallId = "python",
                Tool = ToolKind.Python,
                Content = "from pathlib import Path;import sys,numpy,sympy,matplotlib;matplotlib.use('Agg');import matplotlib.pyplot as p;print(Path('资料.txt').read_text(encoding='utf-8'));print('|'.join(sys.argv[1:]));p.plot([0,1],[0,1]);p.savefig('plot.png')",
                Arguments = ["中文", "a b", @"尾\"],
                WorkingDirectory = input,
                TimeoutMs = 120000
            };
            var directory = Path.Combine(root, "container-worker-python");
            var result = await executor.ExecuteAsync(broker.Request(call)!, directory, CancellationToken.None);
            require(result.Succeeded && result.Stdout.Contains("合成输入") && result.Stdout.Contains(@"中文|a b|尾\")
                && File.Exists(Path.Combine(directory, "results", "work", "plot.png")), $"container Python: {result.Status}; {result.Stdout}{result.Stderr}");
            call = call with { CallId = "shell", Tool = ToolKind.Shell, Content = "param($a,$b,$c);[Console]::WriteLine(($a,$b,$c -join '|'))" };
            result = await executor.ExecuteAsync(broker.Request(call)!, Path.Combine(root, "container-worker-shell"), CancellationToken.None);
            require(result.Succeeded && result.Stdout.Contains(@"中文|a b|尾\"), $"container PowerShell: {result.Status}; {result.Stdout}{result.Stderr}");
            require(File.ReadAllText(Path.Combine(input, "资料.txt")) == "合成输入", "worker changed original input");
        });
    }

    private static string Access(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        return new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access).GetSecurityDescriptorSddlForm(AccessControlSections.Access);
    }

    public static async Task<int> ProbeDescendantAsync(string[] arguments)
    {
        var start = new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in new[] { "--child", "appcontainer" }.Concat(arguments)) start.ArgumentList.Add(argument);
        using var process = System.Diagnostics.Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Console.Write(await output);
        Console.Error.Write(await error);
        return process.ExitCode;
    }

    public static int Probe(string[] arguments)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        if (!Native.OpenProcessToken(Native.GetCurrentProcess(), 8, out var token)) throw new Win32Exception(Marshal.GetLastPInvokeError());
        using (token)
        {
            unsafe
            {
                int isolated = 0;
                if (!Native.GetTokenInformation(token, 29, (nint)(&isolated), sizeof(int), out _) || isolated != 1) throw new InvalidOperationException("missing AppContainer token");
                byte* buffer = stackalloc byte[4096];
                if (!Native.GetTokenInformation(token, 30, (nint)buffer, 4096, out _)) throw new Win32Exception(Marshal.GetLastPInvokeError());
                bool internet = false;
                var groups = (Native.SidAndAttributes*)(buffer + IntPtr.Size);
                for (int i = 0; i < *(uint*)buffer; i++)
                    internet |= new SecurityIdentifier(groups[i].Sid).Value == "S-1-15-3-1";
                if (internet != bool.Parse(arguments[4])) throw new InvalidOperationException("wrong network capabilities");
            }
        }
        Denied(() => File.ReadAllText(arguments[0]));
        Denied(() => File.WriteAllText(arguments[1], "changed"));
        Denied(() => File.WriteAllText(arguments[2], "changed"));
        if (Environment.GetEnvironmentVariable("OMS_SYNTHETIC_SECRET") is not null) throw new InvalidOperationException("inherited secret");
        File.WriteAllText(Path.Combine(arguments[3], "artifact.txt"), File.ReadAllText(arguments[2]));
        Console.WriteLine("isolated");
        return 0;
    }

    private static void Denied(Action action)
    {
        try { action(); } catch (UnauthorizedAccessException) { return; }
        throw new InvalidOperationException("AppContainer file access escaped");
    }

    private static partial class Native
    {
        [StructLayout(LayoutKind.Sequential)] internal struct SidAndAttributes { public nint Sid; public uint Attributes; }
        [LibraryImport("kernel32.dll")] internal static partial nint GetCurrentProcess();
        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool OpenProcessToken(nint process, uint access, out SafeAccessTokenHandle token);
        [LibraryImport("advapi32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool GetTokenInformation(SafeAccessTokenHandle token, int kind, nint information, uint length, out uint returned);
    }
}
