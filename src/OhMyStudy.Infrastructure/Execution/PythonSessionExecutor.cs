using System.Diagnostics;
using System.Text;
using System.Text.Json;
using OhMyStudy.Application.Execution;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Execution;

internal sealed class PythonSessionExecutor(string privateRuntime, WindowsProcessRunner processes)
{
    private readonly Dictionary<string, Session> _sessions = new(StringComparer.Ordinal);
    private bool _cleanupFailed;

    public async Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall authorization, string resultsDirectory, CancellationToken cancellationToken)
    {
        var call = authorization.Consume();
        call.Validate();
        if (_cleanupFailed) throw new IOException("The previous Python worker could not be cleaned up. Reopen the app and retry.");
        using var live = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorization.Revocation);
        live.Token.ThrowIfCancellationRequested();
        var parent = RuntimePaths.CheckedDirectory(Path.GetDirectoryName(resultsDirectory)!, create: true);
        var result = Path.Combine(parent, Path.GetFileName(resultsDirectory));
        if (Directory.Exists(result) || File.Exists(result)) throw new IOException("The call directory already exists. Calls cannot be replayed.");
        RuntimePaths.CheckedDirectory(result, create: true);
        var serialized = DomainJson.Serialize(call);
        if (Encoding.UTF8.GetByteCount(serialized) > 2_097_152) throw new IOException("The execution request is too large. Reduce code or arguments and retry.");
        await File.WriteAllTextAsync(Path.Combine(result, "request.json"), serialized, new UTF8Encoding(false), live.Token).ConfigureAwait(false);
        try
        {
            if (!_sessions.TryGetValue(call.JobId, out var session))
            {
                session = new Session();
                _sessions.Add(call.JobId, session);
                var root = RuntimePaths.CheckedDirectory(Path.Combine(parent, "python-" + Guid.NewGuid().ToString("N")), create: true);
                session.Requests = RuntimePaths.CheckedDirectory(Path.Combine(root, "requests"), create: true);
                session.Results = RuntimePaths.CheckedDirectory(Path.Combine(root, "results"), create: true);
                session.Source = Path.GetFullPath(call.WorkingDirectory);
                session.Sandbox = authorization.Mode == ExecutionMode.Sandbox;
                session.Work = session.Sandbox ? RuntimePaths.CheckedDirectory(Path.Combine(session.Results, "work"), create: true) : session.Source;
                var runtime = RuntimePaths.CheckedDirectory(privateRuntime);
                var executable = RuntimePaths.CheckedFile(Path.Combine(runtime, "python.exe"));
                var worker = RuntimePaths.CheckedFile(Path.Combine(runtime, "execute.py"));
                if (session.Sandbox)
                {
                    session.Profile = AppContainerProfile.Create(Guid.NewGuid(), authorization.SandboxNetworking);
                    session.Profile.GrantDirectory(runtime);
                    session.Profile.GrantDirectory(session.Requests);
                    session.Profile.GrantDirectory(session.Results, true);
                }
                session.Worker = processes.RunAsync(new(executable,
                    ["-I", "-X", "utf8", worker, "--session", session.Requests, session.Results, session.Work, call.JobId],
                    session.Work, WindowsProcessRunner.CreateEnvironment(runtime, session.Results), TimeSpan.FromHours(1), AppContainer: session.Profile), session.Lifetime.Token);
            }
            if (session.Source != Path.GetFullPath(call.WorkingDirectory) || session.Sandbox != (authorization.Mode == ExecutionMode.Sandbox))
                throw new InvalidOperationException("The Python session does not match this task's execution policy or directory.");
            if (session.Sandbox) await SandboxToolExecutor.SnapshotInputsAsync(session.Source, session.Work, live.Token, overwrite: true).ConfigureAwait(false);
            var output = RuntimePaths.CheckedDirectory(Path.Combine(session.Results, call.CallId), create: true);
            var pending = Path.Combine(session.Requests, call.CallId + ".pending");
            await File.WriteAllTextAsync(pending, serialized, new UTF8Encoding(false), live.Token).ConfigureAwait(false);
            File.Move(pending, Path.Combine(session.Requests, call.CallId + ".json"));
            var response = Path.Combine(output, "response.json");
            var clock = Stopwatch.StartNew();
            string? stopped = null;
            while (true)
            {
                if (live.IsCancellationRequested) { stopped = "cancelled"; break; }
                if (OutputLength(output) > 1_048_576) { stopped = "output_limit"; break; }
                if (clock.Elapsed >= TimeSpan.FromMilliseconds(call.TimeoutMs)) { stopped = "timed_out"; break; }
                if (File.Exists(response)) break;
                if (session.Worker!.IsCompleted) { stopped = "failed"; break; }
                await Task.Delay(20, CancellationToken.None).ConfigureAwait(false);
            }
            if (stopped is not null) await CloseJobAsync(call.JobId).ConfigureAwait(false);
            uint? exitCode = null;
            string status;
            if (stopped is null)
            {
                RuntimePaths.CheckedFile(response);
                using var json = JsonDocument.Parse(await File.ReadAllTextAsync(response, live.Token).ConfigureAwait(false));
                status = json.RootElement.GetProperty("status").GetString()!;
                exitCode = json.RootElement.GetProperty("exit_code").GetUInt32();
                if (status is not ("completed" or "failed")) throw new InvalidDataException("The Python worker returned an invalid status.");
            }
            else status = stopped;
            int remaining = stopped is null ? 1_048_576 : 1_048_000;
            string ReadOutput(string name)
            {
                var path = Path.Combine(output, name);
                if (!File.Exists(path)) return "";
                RuntimePaths.CheckedFile(path);
                using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var bytes = new byte[(int)Math.Min(file.Length, remaining)];
                file.ReadExactly(bytes);
                var text = Encoding.UTF8.GetString(bytes);
                int used = 0, length = 0;
                foreach (var rune in text.EnumerateRunes())
                {
                    if (used + rune.Utf8SequenceLength > remaining) break;
                    used += rune.Utf8SequenceLength; length += rune.Utf16SequenceLength;
                }
                remaining -= used;
                return text[..length];
            }
            var stdout = ReadOutput("stdout.txt");
            var stderr = ReadOutput("stderr.txt");
            if (stopped is not null) stderr += "\nPython session reset after " + stopped + ". Recreate variables or reload saved files before continuing.";
            if (session.Sandbox && !live.IsCancellationRequested)
                await SandboxToolExecutor.SnapshotInputsAsync(session.Work, session.Source, live.Token, overwrite: true).ConfigureAwait(false);
            live.Token.ThrowIfCancellationRequested();
            return new(status, exitCode, stdout, stderr);
        }
        catch
        {
            await CloseJobAsync(call.JobId).ConfigureAwait(false);
            throw;
        }
    }

    private static long OutputLength(string directory) => new[] { "stdout.txt", "stderr.txt" }
        .Sum(name => File.Exists(Path.Combine(directory, name)) ? new FileInfo(Path.Combine(directory, name)).Length : 0);

    public async Task CloseJobAsync(string jobId)
    {
        if (!_sessions.Remove(jobId, out var session)) return;
        session.Lifetime.Cancel();
        try
        {
            try { if (session.Worker is not null) await session.Worker.ConfigureAwait(false); }
            finally
            {
                try { session.Profile?.Dispose(); }
                finally { session.Lifetime.Dispose(); }
            }
        }
        catch { _cleanupFailed = true; throw; }
    }

    private sealed class Session
    {
        public CancellationTokenSource Lifetime { get; } = new();
        public Task<ToolExecutionResult>? Worker { get; set; }
        public AppContainerProfile? Profile { get; set; }
        public string Requests { get; set; } = "";
        public string Results { get; set; } = "";
        public string Source { get; set; } = "";
        public string Work { get; set; } = "";
        public bool Sandbox { get; set; }
    }
}
