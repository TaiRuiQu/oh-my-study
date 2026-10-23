using System.Text;
using OhMyStudy.Application.Execution;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Execution;

public sealed class HostToolExecutor(string privateRuntime, WindowsProcessRunner? processRunner = null) : IToolExecutor
{
    private readonly WindowsProcessRunner _processes = processRunner ?? new();
    private readonly PythonSessionExecutor _python = new(privateRuntime, processRunner ?? new());

    public Task CloseJobAsync(string jobId) => _python.CloseJobAsync(jobId);

    public async Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall authorization, string resultsDirectory, CancellationToken cancellationToken)
    {
        if (authorization.Mode == ExecutionMode.Sandbox)
            throw new InvalidOperationException("Sandbox execution cannot fall back to local execution.");
        if (authorization.Call.Tool == ToolKind.Python)
            return await _python.ExecuteAsync(authorization, resultsDirectory, cancellationToken).ConfigureAwait(false);
        var call = authorization.Consume();
        call.Validate();
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorization.Revocation);
        linked.Token.ThrowIfCancellationRequested();
        var parent = RuntimePaths.CheckedDirectory(Path.GetDirectoryName(resultsDirectory)!, create: true);
        var results = Path.Combine(parent, Path.GetFileName(resultsDirectory));
        if (Directory.Exists(results)) throw new IOException("The call directory already exists. Calls cannot be replayed.");
        RuntimePaths.CheckedDirectory(results, create: true);
        string requestFile = Path.Combine(results, "request.json");
        await File.WriteAllTextAsync(requestFile, DomainJson.Serialize(call), new UTF8Encoding(false), linked.Token).ConfigureAwait(false);
        var environment = WindowsProcessRunner.CreateEnvironment(Path.GetFullPath(privateRuntime), results);
        var executable = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "System32", "WindowsPowerShell", "v1.0", "powershell.exe");
        var scriptFile = Path.Combine(results, "approved-shell.ps1");
        var values = string.Join(',', call.Arguments.Select(PowerShellString));
        var script = "$a=@(" + values + ");& ([scriptblock]::Create(" + PowerShellString(call.Content) + ")) @a;if(-not $?){exit 1}";
        await File.WriteAllTextAsync(scriptFile, script, new UTF8Encoding(false), linked.Token).ConfigureAwait(false);
        var literal = "'" + scriptFile.Replace("'", "''", StringComparison.Ordinal) + "'";
        var wrapper = "$ErrorActionPreference='Stop'; [Console]::OutputEncoding=[Text.UTF8Encoding]::new($false); "
            + "Set-Location -LiteralPath (" + PowerShellString(call.WorkingDirectory) + "); "
            + "& ([scriptblock]::Create([IO.File]::ReadAllText(" + literal + "))); if(-not $?){exit 1}";
        string[] arguments = ["-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(wrapper))];
        return await _processes.RunAsync(new(executable, arguments, call.WorkingDirectory, environment,
            TimeSpan.FromMilliseconds(call.TimeoutMs)), linked.Token).ConfigureAwait(false);
    }

    private static string PowerShellString(string value) =>
        "[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String('" + Convert.ToBase64String(Encoding.UTF8.GetBytes(value)) + "'))";
}
