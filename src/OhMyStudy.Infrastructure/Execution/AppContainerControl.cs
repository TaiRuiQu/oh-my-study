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
        if (_profile is not null) throw new InvalidOperationException("AppContainer 正在使用中。");
        var settings = JsonSerializer.Deserialize(configuration, SandboxJson.Default.AppContainerConfiguration)
            ?? throw new InvalidDataException("无效的 AppContainer 配置。");
        string[] directories = [settings.Runtime, settings.Inputs, settings.Requests, settings.Results];
        foreach (var directory in directories) RuntimePaths.CheckedDirectory(directory);
        for (int i = 0; i < directories.Length; i++)
            for (int j = i + 1; j < directories.Length; j++)
                if (Within(directories[i], directories[j]) || Within(directories[j], directories[i]))
                    throw new InvalidDataException("沙箱目录不能重叠。");
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
        if (_profile is null || id != _id || _worker is not null) throw new InvalidOperationException("AppContainer 会话不匹配或已使用。");
        if (!ExecutionCall.ValidId(jobId) || !ExecutionCall.ValidId(nonce)) throw new ArgumentException("无效的会话标识。");
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
        if (!result.Succeeded) throw new IOException("AppContainer 工作进程未能完成，请检查运行组件后重试。");
    }

    public async Task StopAsync(Guid id, CancellationToken cancellationToken)
    {
        if (_profile is null) return;
        if (id != _id) throw new InvalidOperationException("不能停止其他 AppContainer 会话。");
        _lifetime?.Cancel();
        if (_worker is not null)
        {
            try { await _worker.WaitAsync(cancellationToken).ConfigureAwait(false); }
            catch (Exception) when (_worker.IsCompleted) { }
        }
        _profile.Dispose();
        _profile = null;
        _lifetime?.Dispose();
        _lifetime = null;
        _worker = null;
        _configuration = null;
        _id = Guid.Empty;
    }

    private static bool Within(string a, string b) => Path.GetFullPath(a).Equals(Path.GetFullPath(b), StringComparison.OrdinalIgnoreCase)
        || Path.GetFullPath(a).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(b)) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}
