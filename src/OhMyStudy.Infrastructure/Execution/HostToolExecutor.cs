using System.Text;
using OhMyStudy.Application.Execution;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Execution;

public sealed class HostToolExecutor(string privateRuntime, WindowsProcessRunner? processRunner = null) : IToolExecutor
{
    private readonly WindowsProcessRunner _processes = processRunner ?? new();

    public async Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall authorization, string resultsDirectory, CancellationToken cancellationToken)
    {
        if (authorization.Mode == ExecutionMode.Sandbox)
            throw new InvalidOperationException("沙箱执行不可转到本机重试。");
        var call = authorization.Consume();
        call.Validate();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorization.Revocation);
        linked.Token.ThrowIfCancellationRequested();
        var parent = RuntimePaths.CheckedDirectory(Path.GetDirectoryName(resultsDirectory)!, create: true);
        var results = Path.Combine(parent, Path.GetFileName(resultsDirectory));
        if (Directory.Exists(results)) throw new IOException("调用目录已存在，不能重放。");
        RuntimePaths.CheckedDirectory(results, create: true);
        string requestFile = Path.Combine(results, "request.json");
        await File.WriteAllTextAsync(requestFile, DomainJson.Serialize(call), new UTF8Encoding(false), linked.Token).ConfigureAwait(false);
        var environment = WindowsProcessRunner.CreateEnvironment(Path.GetFullPath(privateRuntime), results);
        string executable;
        IReadOnlyList<string> arguments;
        if (call.Tool == ToolKind.Python)
        {
            var runtime = RuntimePaths.CheckedDirectory(privateRuntime);
            executable = RuntimePaths.CheckedFile(Path.Combine(runtime, "python.exe"));
            var worker = RuntimePaths.CheckedFile(Path.Combine(runtime, "execute.py"));
            arguments = ["-I", "-X", "utf8", worker, requestFile];
        }
        else
        {
            executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
            var scriptFile = Path.Combine(results, "approved-shell.ps1");
            var values = string.Join(',', call.Arguments.Select(PowerShellString));
            var script = "$a=@(" + values + ");& ([scriptblock]::Create(" + PowerShellString(call.Content) + ")) @a;if(-not $?){exit 1}";
            await File.WriteAllTextAsync(scriptFile, script, new UTF8Encoding(false), linked.Token).ConfigureAwait(false);
            var literal = "'" + scriptFile.Replace("'", "''", StringComparison.Ordinal) + "'";
            var wrapper = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
                + "& ([scriptblock]::Create([IO.File]::ReadAllText(" + literal + "))); if(-not $?){exit 1}";
            arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper))];
        }
        return await _processes.RunAsync(new(executable, arguments, call.WorkingDirectory, environment,
            TimeSpan.FromMilliseconds(call.TimeoutMs)), linked.Token).ConfigureAwait(false);
    }

    private static string PowerShellString(string value) =>
        "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";
}
