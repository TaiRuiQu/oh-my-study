using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;
using OhMyStudy.Application.Execution;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;
using OhMyStudy.Infrastructure.Execution;

internal static partial class AppContainerChecks
{
    public static async Task RunAsync(string root, Func<string, Func<Task>, Task> check, Action<bool, string> require)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var runtime = Path.Combine(root, "container-runtime"); Directory.CreateDirectory(runtime);
        var executable = Path.Combine(runtime, "probe.exe"); File.Copy(Environment.ProcessPath!, executable);
        var inputs = Path.Combine(root, "container-inputs"); Directory.CreateDirectory(inputs);
        var input = Path.Combine(inputs, "资料.txt"); await File.WriteAllTextAsync(input, "合成输入");
        var secret = Path.Combine(root, "container-private.txt"); await File.WriteAllTextAsync(secret, "must-not-read");
        var immutable = Path.Combine(runtime, "immutable.txt"); await File.WriteAllTextAsync(immutable, "read-only");
        var inputSecurity = new DirectoryInfo(inputs).GetAccessControl(AccessControlSections.Access);
        inputSecurity.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        new DirectoryInfo(inputs).SetAccessControl(inputSecurity);
        ClearAutoInheritanceMarker(runtime);
        foreach (bool network in new[] { false, true })
        {
            await check("appcontainer-token-files-environment-and-network-" + network, async () =>
            {
                var results = Path.Combine(root, "container-results-" + network); Directory.CreateDirectory(results);
                var before = new[] { Access(runtime), Access(inputs), Access(results) };
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
                require(new[] { Access(runtime), Access(inputs), Access(results) }.SequenceEqual(before), "directory access rules changed");
                require(File.ReadAllText(immutable) == "read-only" && File.ReadAllText(input) == "合成输入", "inputs changed");
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
        await check("appcontainer-cleanup-after-results-removed", () =>
        {
            var results = Path.Combine(root, "container-removed-results"); Directory.CreateDirectory(results);
            var before = new[] { Access(runtime), Access(inputs) };
            var profile = AppContainerProfile.Create(Guid.NewGuid());
            try
            {
                profile.GrantDirectory(runtime); profile.GrantDirectory(inputs); profile.GrantDirectory(results, true);
                Directory.Delete(results);
                profile.Dispose();
            }
            finally { Directory.CreateDirectory(results); profile.Dispose(); }
            require(new[] { Access(runtime), Access(inputs) }.SequenceEqual(before), "removed results left directory grants behind");
            return Task.CompletedTask;
        });
        await check("appcontainer-cleanup-after-worker-start-fault", async () =>
        {
            var requests = Path.Combine(root, "container-fault-requests"); Directory.CreateDirectory(requests);
            var results = Path.Combine(root, "container-fault-results"); Directory.CreateDirectory(results);
            var workerRuntime = Path.Combine(root, "container-fault-runtime"); Directory.CreateDirectory(workerRuntime);
            var python = Path.Combine(workerRuntime, "python.exe"); File.Copy(executable, python);
            await File.WriteAllTextAsync(Path.Combine(workerRuntime, "sandbox-worker.py"), "synthetic");
            var before = new[] { Access(workerRuntime), Access(inputs), Access(requests), Access(results) };
            var control = new AppContainerControl();
            var id = Guid.NewGuid();
            var configuration = JsonSerializer.Serialize(new AppContainerConfiguration(workerRuntime, inputs, requests, results, false), SandboxJson.Default.AppContainerConfiguration);
            try
            {
                await control.StartAsync(id, configuration, CancellationToken.None);
                File.Move(python, python + ".saved");
                try { await control.RunWorkerAsync(id, "fault", "nonce", CancellationToken.None); throw new Exception("missing worker executable accepted"); }
                catch (FileNotFoundException) { }
            }
            finally { await control.StopAsync(id, CancellationToken.None); }
            require(!await control.HasSessionsAsync(CancellationToken.None), "failed worker retained its profile");
            require(new[] { Access(workerRuntime), Access(inputs), Access(requests), Access(results) }.SequenceEqual(before), "failed worker retained directory grants");
            File.Move(python + ".saved", python);
            id = Guid.NewGuid();
            try { await control.StartAsync(id, configuration, CancellationToken.None); }
            finally { await control.StopAsync(id, CancellationToken.None); }
        });
        await check("appcontainer-cleanup-attempts-all-grants", () =>
        {
            var results = Path.Combine(root, "container-blocked-results"); Directory.CreateDirectory(results);
            var before = new[] { Access(runtime), Access(inputs) };
            var profile = AppContainerProfile.Create(Guid.NewGuid());
            try
            {
                profile.GrantDirectory(runtime); profile.GrantDirectory(inputs); profile.GrantDirectory(results, true);
                Directory.Move(results, results + ".saved");
                File.WriteAllText(results, "synthetic blocker");
                try { profile.Dispose(); throw new Exception("replaced grant directory accepted"); }
                catch (IOException) { }
                require(new[] { Access(runtime), Access(inputs) }.SequenceEqual(before), "one failed restoration prevented revoking other grants");
            }
            finally
            {
                if (File.Exists(results)) File.Delete(results);
                if (Directory.Exists(results + ".saved")) Directory.Move(results + ".saved", results);
                profile.Dispose();
            }
            return Task.CompletedTask;
        });
        await check("appcontainer-public-runtime-keeps-existing-read-grant", () =>
        {
            var shared = Path.Combine(root, "container-public-runtime"); Directory.CreateDirectory(shared);
            var security = new DirectoryInfo(shared).GetAccessControl(AccessControlSections.Access);
            security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-15-2-1"), FileSystemRights.ReadAndExecute,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(shared).SetAccessControl(security);
            var before = Access(shared);
            using var profile = AppContainerProfile.Create(Guid.NewGuid());
            profile.GrantDirectory(shared);
            require(Access(shared) == before, "public runtime unnecessarily requested ACL write access");
            return Task.CompletedTask;
        });
    }

    public static async Task RunWorkerAsync(string runtime, string root, Func<string, Func<Task>, Task> check, Action<bool, string> require)
    {
        var input = Path.Combine(root, "container-worker-input"); Directory.CreateDirectory(input);
        await File.WriteAllTextAsync(Path.Combine(input, "资料.txt"), "合成输入");
        var capability = await SandboxCapability.ProbeAsync(runtime);
        async Task<ToolExecutionResult> ExecuteAsync(string id, ToolKind tool, string content, ulong timeout = 120000, CancellationToken cancellationToken = default)
        {
            require(capability.State == CapabilityState.Ready, capability.Reason);
            using var broker = new PermissionBroker();
            broker.RegisterJob(id, ExecutionPolicy.Freeze(ExecutionMode.Sandbox, capability, false));
            var call = new ExecutionCall
            {
                ProtocolVersion = 1,
                JobId = id,
                CallId = id,
                Tool = tool,
                Content = content,
                Arguments = ["中文", "a b", @"尾\"],
                WorkingDirectory = input,
                TimeoutMs = timeout
            };
            return await new SandboxToolExecutor(runtime).ExecuteAsync(broker.Request(call)!, Path.Combine(root, "container-worker-" + id), cancellationToken);
        }
        await check("appcontainer-private-python", async () =>
        {
            var result = await ExecuteAsync("python", ToolKind.Python,
                "from pathlib import Path;import os,sys,numpy,sympy,matplotlib;assert sys.stdin.read() == '';assert all(os.environ.get(k) for k in ('USERPROFILE','LOCALAPPDATA','APPDATA'));assert 'OMS_SYNTHETIC_SECRET' not in os.environ;matplotlib.use('Agg');import matplotlib.pyplot as p;print(Path('资料.txt').read_text(encoding='utf-8'));print('|'.join(sys.argv[1:]));Path('资料.txt').write_text('临时修改',encoding='utf-8');p.plot([0,1],[0,1]);p.savefig('plot.png')");
            require(result.Succeeded && result.Stdout.Contains("合成输入") && result.Stdout.Contains(@"中文|a b|尾\")
                && File.Exists(Path.Combine(root, "container-worker-python", "results", "work", "plot.png")), $"container Python: {result.Status}; {result.Stdout}{result.Stderr}");
            require(File.ReadAllText(Path.Combine(input, "资料.txt")) == "合成输入", "worker changed original input");
        });
        await check("appcontainer-private-powershell", async () =>
        {
            var result = await ExecuteAsync("shell", ToolKind.Shell, "param($a,$b,$c);[Console]::WriteLine(($a,$b,$c -join '|'));[Console]::Error.WriteLine('中文错误流')");
            require(result.Succeeded && result.Stdout.Contains(@"中文|a b|尾\"), $"container PowerShell: {result.Status}; {result.Stdout}{result.Stderr}");
            require(result.Stderr.Contains("中文错误流"), "PowerShell stderr encoding changed");
        });
        foreach (var item in new (string Id, ToolKind Tool, string Code, string Status, uint? Exit, ulong Timeout)[]
        {
            ("python-nonzero", ToolKind.Python, "raise SystemExit(7)", "failed", 7, 120000),
            ("shell-nonzero", ToolKind.Shell, "exit 7", "failed", 7, 120000),
            ("shell-error", ToolKind.Shell, "throw 'synthetic error'", "failed", 1, 120000),
            ("python-timeout", ToolKind.Python, "import time;time.sleep(30)", "timed_out", null, 150),
            ("shell-timeout", ToolKind.Shell, "[Threading.Thread]::Sleep(30000)", "timed_out", null, 150),
            ("python-output-limit", ToolKind.Python, "import sys;sys.stdout.write('你'*250000);sys.stdout.flush();sys.stderr.write('🙂'*250000);sys.stderr.flush()", "output_limit", null, 120000)
        })
        {
            await check("appcontainer-" + item.Id, async () =>
            {
                var result = await ExecuteAsync(item.Id, item.Tool, item.Code, item.Timeout);
                require(result.Status == item.Status && (item.Exit is null || result.ExitCode == item.Exit), $"{item.Id}: {result.Status}; exit={result.ExitCode}; {result.Stderr}");
                require(System.Text.Encoding.UTF8.GetByteCount(result.Stdout + result.Stderr) <= 1_048_576, "output exceeded byte limit");
            });
        }
        await check("appcontainer-cancel-and-recover", async () =>
        {
            using var cancel = new CancellationTokenSource(500);
            try { await ExecuteAsync("python-cancel", ToolKind.Python, "import time;time.sleep(30)", cancellationToken: cancel.Token); throw new Exception("cancelled computation completed"); }
            catch (OperationCanceledException) { }
            var result = await ExecuteAsync("python-after-cancel", ToolKind.Python, "print('recovered')");
            require(result.Succeeded && result.Stdout.Contains("recovered"), "cancellation prevented later computation");
        });
        await check("appcontainer-worker-descendant-cleanup", async () =>
        {
            var result = await ExecuteAsync("python-descendant", ToolKind.Python,
                "import subprocess,sys;p=subprocess.Popen([sys.executable,'-I','-c','import time;time.sleep(60)'],stdin=sys.stdin,stdout=sys.stdout,stderr=sys.stderr);print(p.pid,flush=True)");
            require(result.Succeeded, $"descendant launch: {result.Status}; {result.Stderr}");
            try
            {
                using var process = System.Diagnostics.Process.GetProcessById(int.Parse(result.Stdout.Trim()));
                await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            catch (ArgumentException) { }
        });
    }

    private static string Access(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        return security.AreAccessRulesProtected + "|" + string.Join(";", security.GetAccessRules(true, true, typeof(SecurityIdentifier))
            .OfType<FileSystemAccessRule>().Select(rule => $"{rule.IdentityReference.Value}:{rule.AccessControlType}:{rule.FileSystemRights}:{rule.InheritanceFlags}:{rule.PropagationFlags}:{rule.IsInherited}"));
    }

    private static unsafe void ClearAutoInheritanceMarker(string directory)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var security = new DirectoryInfo(directory).GetAccessControl(AccessControlSections.Access);
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        descriptor.SetFlags(descriptor.ControlFlags & ~ControlFlags.DiscretionaryAclAutoInherited);
        var bytes = new byte[descriptor.BinaryLength];
        descriptor.GetBinaryForm(bytes, 0);
        fixed (byte* pointer = bytes)
            if (!Native.SetFileSecurityW(directory, 4, (nint)pointer)) throw new Win32Exception(Marshal.GetLastPInvokeError());
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
        [LibraryImport("advapi32.dll", StringMarshalling = StringMarshalling.Utf16, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static partial bool SetFileSecurityW(string name, uint information, nint descriptor);
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
