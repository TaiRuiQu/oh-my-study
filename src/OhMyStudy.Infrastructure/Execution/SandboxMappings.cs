using System.Text.Json;
using System.Text.Json.Serialization;
using OhMyStudy.Domain;

namespace OhMyStudy.Infrastructure.Execution;

public sealed class SandboxMappings
{
    private readonly string[] _directories;
    private SandboxMappings(string[] directories) => _directories = directories;

    public static SandboxMappings ForTask(string privateRuntime, string taskRoot, string jobId)
    {
        if (!ExecutionCall.ValidId(jobId)) throw new ArgumentException("Invalid task ID.");
        var root = RuntimePaths.CheckedDirectory(taskRoot);
        var task = RuntimePaths.CheckedDirectory(Path.Combine(root, jobId));
        var paths = new[] { RuntimePaths.CheckedDirectory(privateRuntime), RuntimePaths.CheckedDirectory(Path.Combine(task, "inputs")),
            RuntimePaths.CheckedDirectory(Path.Combine(task, "requests")), RuntimePaths.CheckedDirectory(Path.Combine(task, "results")) };
        for (int i = 0; i < paths.Length; i++)
        {
            if (paths[i].StartsWith(@"\\", StringComparison.Ordinal)) throw new IOException("AppContainer requires a local directory.");
            for (int j = i + 1; j < paths.Length; j++)
                if (Within(paths[i], paths[j]) || Within(paths[j], paths[i])) throw new IOException("Sandbox mapping directories must not overlap.");
        }
        return new(paths);
    }

    public string ToConfiguration(string jobId, string nonce, bool networking)
    {
        if (!ExecutionCall.ValidId(jobId) || !ExecutionCall.ValidId(nonce)) throw new ArgumentException("Invalid session ID.");
        return JsonSerializer.Serialize(new AppContainerConfiguration(_directories[0], _directories[1], _directories[2], _directories[3], networking),
            SandboxJson.Default.AppContainerConfiguration);
    }
    private static bool Within(string a, string b) => a.Equals(b, StringComparison.OrdinalIgnoreCase)
        || a.StartsWith(Path.TrimEndingDirectorySeparator(b) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record AppContainerConfiguration(string Runtime, string Inputs, string Requests, string Results, bool Networking);
