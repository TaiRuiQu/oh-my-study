using System.ComponentModel;
using System.Text.Json;
using OhMyStudy.Application.Jobs;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Execution;

public interface ISandboxControl
{
    Task<bool> HasSessionsAsync(CancellationToken cancellationToken);
    Task StartAsync(Guid id, string configuration, CancellationToken cancellationToken);
    Task RunWorkerAsync(Guid id, string jobId, string nonce, CancellationToken cancellationToken);
    Task StopAsync(Guid id, CancellationToken cancellationToken);
}

public sealed class AppContainerControl : ISandboxControl
{
    private Guid _id;
    private AppContainerConfiguration? _configuration;
    private AppContainerProfile? _profile;
    private CancellationTokenSource? _lifetime;
    private Task<ToolExecutionResult>? _worker;

    public Task<bool> HasSessionsAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(_profile is not null);
    }

    public Task StartAsync(Guid id, string configuration, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_profile is not null) throw new InvalidOperationException("AppContainer is in use.");
        var settings = JsonSerializer.Deserialize(configuration, SandboxJson.Default.AppContainerConfiguration)
            ?? throw new InvalidDataException("Invalid AppContainer configuration.");
        string[] directories = [settings.Runtime, settings.Inputs, settings.Requests, settings.Results];
        foreach (var directory in directories) RuntimePaths.CheckedDirectory(directory);
        for (int i = 0; i < directories.Length; i++)
            for (int j = i + 1; j < directories.Length; j++)
                if (Within(directories[i], directories[j]) || Within(directories[j], directories[i]))
                    throw new InvalidDataException("Sandbox directories must not overlap.");
        RuntimePaths.CheckedFile(Path.Combine(settings.Runtime, "python.exe"));
        RuntimePaths.CheckedFile(Path.Combine(settings.Runtime, "sandbox-worker.py"));
        _profile = AppContainerProfile.Create(id, settings.Networking);
        _id = id;
        _configuration = settings;
        foreach (var directory in directories)
        {
            cancellationToken.ThrowIfCancellationRequested();
            _profile.GrantDirectory(directory, directory == settings.Results);
        }
        return Task.CompletedTask;
    }

    public async Task RunWorkerAsync(Guid id, string jobId, string nonce, CancellationToken cancellationToken)
    {
        if (_profile is null || id != _id || _worker is not null) throw new InvalidOperationException("The AppContainer session does not match or has already been used.");
        if (!ExecutionCall.ValidId(jobId) || !ExecutionCall.ValidId(nonce)) throw new ArgumentException("Invalid session ID.");
        var settings = _configuration!;
        _lifetime = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var request = new ProcessRequest(Path.Combine(settings.Runtime, "python.exe"),
            ["-I", "-X", "utf8", Path.Combine(settings.Runtime, "sandbox-worker.py"),
                "--job-id", jobId, "--nonce", nonce, "--runtime", settings.Runtime,
                "--inputs", settings.Inputs, "--requests", settings.Requests, "--results", settings.Results],
            settings.Results, WindowsProcessRunner.CreateEnvironment(settings.Runtime, settings.Results), TimeSpan.FromHours(1), AppContainer: _profile);
        _worker = new WindowsProcessRunner().RunAsync(request, _lifetime.Token);
        var result = await _worker.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!result.Succeeded) throw new IOException("The AppContainer worker did not complete. Check runtime components and retry.");
    }

    public async Task StopAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_profile is null) return;
        if (id != _id) throw new InvalidOperationException("Cannot stop another AppContainer session.");
        _lifetime?.Cancel();
        Exception? failure = null;
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception error) when (_worker.IsCompleted) { failure = error; }
        }
        _profile.Dispose();
        _profile = null;
        _lifetime?.Dispose();
        _lifetime = null;
        _worker = null;
        _configuration = null;
        _id = Guid.Empty;
        if (failure is not null && failure is not (OperationCanceledException or Win32Exception or FileNotFoundException or DirectoryNotFoundException))
            throw new IOException("The AppContainer worker could not stop safely. Reopen the app and retry.", failure);
    }

    private static bool Within(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(a).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
