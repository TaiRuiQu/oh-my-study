using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace OhMyStudy.Domain;

[JsonConverter(typeof(StrictEnumConverter<ExecutionMode>))]
public enum ExecutionMode { RequestPermission, Sandbox, BypassPermission }
[JsonConverter(typeof(StrictEnumConverter<CapabilityState>))]
public enum CapabilityState { Ready, Unsupported, NeedsEnable, NeedsReboot, VirtualizationUnavailable, PolicyBlocked, Busy, Unknown, StartFailed }
[JsonConverter(typeof(StrictEnumConverter<ExecutorKind>))]
public enum ExecutorKind { Host, WindowsSandbox }
[JsonConverter(typeof(StrictEnumConverter<ToolKind>))]
public enum ToolKind { Python, Shell }
[JsonConverter(typeof(StrictEnumConverter<JobState>))]
public enum JobState { Queued, Running, WaitingPermission, Completed, Failed, Cancelled, Interrupted }
[JsonConverter(typeof(StrictEnumConverter<DiagnosticKind>))]
public enum DiagnosticKind { Storage, Permission, Grading, References }
[JsonConverter(typeof(StrictEnumConverter<PermissionChoice>))]
public enum PermissionChoice { AllowOnce, AllowTask, Deny }

public static class JobStateExtensions
{
    public static bool IsTerminal(this JobState state) => state is JobState.Completed or JobState.Failed or JobState.Cancelled or JobState.Interrupted;
}

public sealed record Capability
{
    public required CapabilityState State { get; init; }
    public required string Reason { get; init; }
}

public sealed record ExecutionPolicy
{
    public required ExecutionMode PreferredMode { get; init; }
    public required ExecutionMode EffectiveMode { get; init; }
    public required ExecutorKind ExecutorKind { get; init; }
    public string? FallbackReason { get; init; }
    public string? Warning { get; init; }
    public required bool SandboxNetworking { get; init; }
    public static ExecutionPolicy Freeze(ExecutionMode? preferred, Capability capability, bool networking)
    {
        var preference = preferred ?? ExecutionMode.Sandbox;
        bool fallback = preference == ExecutionMode.Sandbox && capability.State != CapabilityState.Ready;
        var effective = fallback ? ExecutionMode.BypassPermission : preference;
        return new()
        {
            PreferredMode = preference,
            EffectiveMode = effective,
            ExecutorKind = effective == ExecutionMode.Sandbox ? ExecutorKind.WindowsSandbox : ExecutorKind.Host,
            FallbackReason = fallback ? capability.Reason : null,
            Warning = fallback ? $"{capability.Reason}。本次代码与命令将在本机使用当前账户权限执行，可访问该账户的文件和网络。本机执行／未隔离。" : null,
            SandboxNetworking = networking
        };
    }
}

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record ExecutionCall
{
    public required uint ProtocolVersion { get; init; }
    public required string JobId { get; init; }
    public required string CallId { get; init; }
    public required ToolKind Tool { get; init; }
    public required string Content { get; init; }
    public required List<string> Arguments { get; init; }
    public required string WorkingDirectory { get; init; }
    public required ulong TimeoutMs { get; init; }

    public void Validate()
    {
        DomainValidationException.Require(ProtocolVersion == 1 && ValidId(JobId) && ValidId(CallId), "invalid protocol version or call identity");
        DomainValidationException.Require(TimeoutMs is > 0 and <= 3_600_000 && Encoding.UTF8.GetByteCount(Content) <= 1_048_576, "invalid timeout or oversized code");
        DomainValidationException.Require(Arguments.Count <= 128 && Arguments.Sum(a => (long)Encoding.UTF8.GetByteCount(a)) <= 65_536, "argument count or size limit exceeded");
        DomainValidationException.Require(Path.IsPathFullyQualified(WorkingDirectory), "working directory must be absolute");
    }

    public static bool ValidId(string value) => value.Length is > 0 and <= 128 && value.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= '0' and <= '9' or '-' or '_');

    // The reviewed hash is serde_json's exact UTF-8 form, independent of serializer encoder settings.
    public string Digest() => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(CanonicalJson())));
    public string CanonicalJson() => $"{{\"protocol_version\":{ProtocolVersion},\"job_id\":{SerdeJson.Quote(JobId)},\"call_id\":{SerdeJson.Quote(CallId)},\"tool\":{SerdeJson.Quote(Tool switch { ToolKind.Python => "python", ToolKind.Shell => "shell", _ => throw new JsonException("Invalid tool") })},\"content\":{SerdeJson.Quote(Content)},\"arguments\":[{string.Join(',', Arguments.Select(SerdeJson.Quote))}],\"working_directory\":{SerdeJson.Quote(WorkingDirectory)},\"timeout_ms\":{TimeoutMs}}}";
}

public sealed record PermissionReview
{
    public required ExecutionCall Call { get; init; }
    public required string Digest { get; init; }
}

public sealed record Job
{
    public required string Id { get; init; }
    public required DiagnosticKind Kind { get; init; }
    public required JobState State { get; init; }
    public required ulong CreatedAt { get; init; }
    public required ulong UpdatedAt { get; init; }
    public required double Progress { get; init; }
    public required string Message { get; init; }
    public required ulong Sequence { get; init; }
    public required ExecutionPolicy ExecutionPolicy { get; init; }
    public PermissionReview? PendingPermission { get; init; }
    public string? Error { get; init; }
    public GradingInput? Input { get; init; }
}

public sealed record JobEvent
{
    public required string Id { get; init; }
    public required string JobId { get; init; }
    public required ulong Sequence { get; init; }
    public required ulong CreatedAt { get; init; }
    public required string Kind { get; init; }
    public required JsonElement Payload { get; init; }
}

/// <summary>A durable, public execution step. Never stores provider-private thinking or credentials.</summary>
public sealed record JobActivity
{
    public required string Key { get; init; }
    public required string Kind { get; init; }
    public required string Title { get; init; }
    public string Detail { get; init; } = "";
    public string Text { get; init; } = "";
    public string State { get; init; } = "running";
    public string? PageId { get; init; }
    public int PagesRead { get; init; }
    public int QuestionsSaved { get; init; }
    public long ElapsedMs { get; init; }
}

public sealed class DomainValidationException(string message) : Exception(message)
{
    public static void Require(bool condition, string message) { if (!condition) throw new DomainValidationException(message); }
}
