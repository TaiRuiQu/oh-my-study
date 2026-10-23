using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OhMyStudy.Application.Execution;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Execution;

public sealed class SandboxToolExecutor(string privateRuntime, ISandboxControl? control = null) : IToolExecutor
{
    private static readonly SemaphoreSlim SessionGate = new(1, 1);
    private static bool _cleanupFailed;
    public async Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall authorization, string resultsDirectory, CancellationToken cancellationToken)
    {
        if (authorization.Mode != ExecutionMode.Sandbox) throw new InvalidOperationException("This permission does not allow sandbox execution.");
        var call = authorization.Consume(); call.Validate();
        using var live = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, authorization.Revocation);
        await SessionGate.WaitAsync(live.Token).ConfigureAwait(false);
        try
        {
            if (_cleanupFailed) throw new IOException("The previous AppContainer could not be cleaned up. Reopen the app and retry.");
            var sandbox = control ?? new AppContainerControl();
            if (await sandbox.HasSessionsAsync(live.Token).ConfigureAwait(false)) throw new IOException("The sandbox is in use. Wait for it to finish and retry.");
            RuntimePaths.CheckedFile(Path.Combine(privateRuntime, "python.exe"));
            RuntimePaths.CheckedFile(Path.Combine(privateRuntime, "execute.py"));
            RuntimePaths.CheckedFile(Path.Combine(privateRuntime, "sandbox-worker.py"));
            var parent = RuntimePaths.CheckedDirectory(Path.GetDirectoryName(resultsDirectory)!, create: true);
            var root = Path.Combine(parent, Path.GetFileName(resultsDirectory));
            if (Directory.Exists(root) || File.Exists(root)) throw new IOException("The call directory already exists. Calls cannot be replayed.");
            RuntimePaths.CheckedDirectory(root, create: true);
            foreach (var name in new[] { "inputs", "requests", "results" }) RuntimePaths.CheckedDirectory(Path.Combine(root, name), create: true);
            await SnapshotInputsAsync(call.WorkingDirectory, Path.Combine(root, "inputs"), live.Token).ConfigureAwait(false);
            var id = Guid.NewGuid(); var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(32));
            var protocol = new SandboxSession(call.JobId, nonce); protocol.AwaitHandshake();
            var request = new SandboxRequest(1, call.JobId, call.CallId, nonce, call.Digest(), call);
            var serializedRequest = JsonSerializer.Serialize(request, SandboxJson.Default.SandboxRequest);
            if (Encoding.UTF8.GetByteCount(serializedRequest) > 2_097_152) throw new IOException("The execution request is too large. Reduce code or arguments and retry.");
            await WriteNewAsync(Path.Combine(root, "requests", "request.json"), serializedRequest, live.Token).ConfigureAwait(false);
            var ownership = Path.Combine(root, "ownership.json");
            await WriteNewAsync(ownership, JsonSerializer.Serialize(new SandboxOwnership(id.ToString("D"), call.JobId, call.CallId, "starting"), SandboxJson.Default.SandboxOwnership), live.Token).ConfigureAwait(false);
            var mappings = SandboxMappings.ForTask(privateRuntime, parent, Path.GetFileName(root));
            using var workerLifetime = CancellationTokenSource.CreateLinkedTokenSource(live.Token);
            Task? worker = null; bool startIssued = false;
            try
            {
                startIssued = true;
                await sandbox.StartAsync(id, mappings.ToConfiguration(call.JobId, nonce, authorization.SandboxNetworking), live.Token).ConfigureAwait(false);
                worker = sandbox.RunWorkerAsync(id, call.JobId, nonce, workerLifetime.Token);
                var handshakePath = Path.Combine(root, "results", "handshake.json");
                await WaitForFileAsync(handshakePath, worker, TimeSpan.FromMinutes(2), live.Token).ConfigureAwait(false);
                var handshake = ReadMessage(handshakePath);
                ValidateMessage(handshake, request);
                if (handshake.Status != "ready") throw new InvalidDataException("The sandbox could not be prepared.");
                protocol.Handshake(handshake.ProtocolVersion, handshake.JobId, handshake.Nonce); protocol.BeginCall(call.CallId);
                using var execution = CancellationTokenSource.CreateLinkedTokenSource(live.Token);
                execution.CancelAfter(TimeSpan.FromMilliseconds(call.TimeoutMs) + TimeSpan.FromSeconds(5));
                try
                {
                    await WaitForFileAsync(Path.Combine(root, "results", "response.json"), worker,
                    TimeSpan.FromMilliseconds(call.TimeoutMs) + TimeSpan.FromSeconds(5), execution.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!live.IsCancellationRequested) { return new("timed_out", null, "", "Execution timed out."); }
                catch (TimeoutException) { return new("timed_out", null, "", "Execution timed out."); }
            }
            finally
            {
                workerLifetime.Cancel();
                protocol.Disconnect();
                if (startIssued)
                {
                    using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(35));
                    try
                    {
                        await sandbox.StopAsync(id, cleanup.Token).ConfigureAwait(false);
                        await File.WriteAllTextAsync(ownership, JsonSerializer.Serialize(new SandboxOwnership(id.ToString("D"), call.JobId, call.CallId, "stopped"), SandboxJson.Default.SandboxOwnership), new UTF8Encoding(false), cleanup.Token).ConfigureAwait(false);
                    }
                    catch (Exception error)
                    {
                        _cleanupFailed = true;
                        throw new IOException("AppContainer could not be cleaned up. Reopen the app and retry.", error);
                    }
                }
                if (worker is not null)
                    try { await worker.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false); }
                    catch (Exception) { }
            }
            live.Token.ThrowIfCancellationRequested();
            var response = ReadMessage(Path.Combine(root, "results", "response.json")); ValidateMessage(response, request);
            if (response.Status is not ("completed" or "failed" or "timed_out" or "output_limit")) throw new InvalidDataException("The sandbox returned an invalid result.");
            if (response.Status == "completed" && response.ExitCode != 0 || response.Status == "failed" && response.ExitCode == 0)
                throw new InvalidDataException("The sandbox returned inconsistent states.");
            if (response.Stdout is null || response.Stderr is null || Encoding.UTF8.GetByteCount(response.Stdout + response.Stderr) > 1_048_576)
                throw new InvalidDataException("The sandbox output is too large or invalid.");
            var work = Path.Combine(root, "results", "work");
            if (Directory.Exists(work)) await SnapshotInputsAsync(work, call.WorkingDirectory, live.Token, overwrite: true).ConfigureAwait(false);
            return new(response.Status, response.ExitCode, response.Stdout, response.Stderr);
        }
        finally { SessionGate.Release(); }
    }

    private static async Task WaitForFileAsync(string path, Task worker, TimeSpan timeout, CancellationToken token)
    {
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(token); limit.CancelAfter(timeout);
        try
        {
            while (!File.Exists(path))
            {
                limit.Token.ThrowIfCancellationRequested();
                if (worker.IsFaulted || worker.IsCanceled) await worker.ConfigureAwait(false);
                if (worker.IsCompleted) throw new IOException("The AppContainer worker exited without returning an execution result.");
                await Task.Delay(50, limit.Token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException error) when (!token.IsCancellationRequested)
        { throw new TimeoutException("Waiting for the sandbox result timed out. Check the sandbox and retry.", error); }
    }
    private static SandboxMessage ReadMessage(string path)
    {
        RuntimePaths.CheckedFile(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length > 8_388_608) throw new InvalidDataException("The sandbox output is too large.");
        var bytes = new byte[checked((int)file.Length)]; file.ReadExactly(bytes);
        try { return JsonSerializer.Deserialize(bytes, SandboxJson.Default.SandboxMessage) ?? throw new InvalidDataException("The sandbox output is invalid."); }
        catch (JsonException error) { throw new InvalidDataException("The sandbox output is invalid.", error); }
    }
    private static void ValidateMessage(SandboxMessage message, SandboxRequest request)
    {
        if (message.ProtocolVersion != 1 || message.JobId != request.JobId || message.CallId != request.CallId || message.Nonce != request.Nonce || message.Digest != request.Digest)
            throw new InvalidDataException("Sandbox session verification failed.");
    }
    private static async Task WriteNewAsync(string path, string value, CancellationToken token)
    {
        await using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.Asynchronous);
        await stream.WriteAsync(Encoding.UTF8.GetBytes(value), token).ConfigureAwait(false);
        await stream.FlushAsync(token).ConfigureAwait(false);
    }
    private static async Task SnapshotInputsAsync(string source, string destination, CancellationToken token, bool overwrite = false)
    {
        var input = RuntimePaths.CheckedDirectory(source); long bytes = 0; int files = 0, entries = 0;
        async Task CopyAsync(string from, string to, int depth)
        {
            if (depth > 8) throw new IOException("The working directory is nested too deeply. Use a separate task directory.");
            foreach (var entry in Directory.EnumerateFileSystemEntries(from))
            {
                token.ThrowIfCancellationRequested();
                if (++entries > 512) throw new IOException("The working directory has too many files. Keep only those needed for this task.");
                if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("The working directory must not contain links.");
                if (Path.GetFullPath(destination).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(entry)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                    || Path.GetFullPath(entry).Equals(destination, StringComparison.OrdinalIgnoreCase))
                    throw new IOException("Use a separate working directory for the task.");
                var target = Path.Combine(to, Path.GetFileName(entry));
                if (Directory.Exists(entry)) { RuntimePaths.CheckedDirectory(target, create: true); await CopyAsync(entry, target, depth + 1).ConfigureAwait(false); }
                else
                {
                    RuntimePaths.CheckedFile(entry);
                    await using var read = new FileStream(entry, FileMode.Open, FileAccess.Read, FileShare.Read);
                    bytes += read.Length;
                    if (++files > 256 || bytes > 268_435_456) throw new IOException("The working directory is too large. Keep only files needed for this task.");
                    if (overwrite && File.Exists(target)) RuntimePaths.CheckedFile(target);
                    await using var write = new FileStream(target, overwrite ? FileMode.Create : FileMode.CreateNew, FileAccess.Write, FileShare.None);
                    await read.CopyToAsync(write, token).ConfigureAwait(false);
                }
            }
        }
        await CopyAsync(input, destination, 0).ConfigureAwait(false);
    }
}

public sealed class PolicyToolExecutor(IToolExecutor host, IToolExecutor sandbox) : IToolExecutor
{
    public Task<ToolExecutionResult> ExecuteAsync(AuthorizedCall call, string resultsDirectory, CancellationToken cancellationToken) =>
        (call.Mode == ExecutionMode.Sandbox ? sandbox : host).ExecuteAsync(call, resultsDirectory, cancellationToken);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SandboxRequest(uint ProtocolVersion, string JobId, string CallId, string Nonce, string Digest, ExecutionCall Call);
[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record SandboxMessage(uint ProtocolVersion, string JobId, string CallId, string Nonce, string Digest, string Status, uint? ExitCode, string Stdout, string Stderr);
public sealed record SandboxOwnership(string Id, string JobId, string CallId, string State);
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower, MaxDepth = 16)]
[JsonSerializable(typeof(SandboxRequest))]
[JsonSerializable(typeof(SandboxMessage))]
[JsonSerializable(typeof(SandboxOwnership))]
[JsonSerializable(typeof(AppContainerConfiguration))]
public sealed partial class SandboxJson : JsonSerializerContext;
