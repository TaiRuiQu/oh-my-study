using System.Text.Json.Nodes;

namespace OhMyStudy.Application.Diagnostics;

public static class OperationDiagnostics
{
    private static readonly AsyncLocal<string?> Current = new();
    private static long sequence;
    public static event Action<string, string, bool>? Written;
    public static bool IsEnabled => Written is not null;
    public static string? CorrelationId => Current.Value;

    public static IDisposable Begin(string correlationId)
    {
        var previous = Current.Value;
        Current.Value = correlationId;
        return new Scope(previous);
    }

    public static void Record(string phase, JsonObject fields, bool failed = false)
    {
        if (Written is not { } writers) return;
        try
        {
            fields["sequence"] = Interlocked.Increment(ref sequence);
            fields["correlation_id"] ??= Current.Value ?? Guid.NewGuid().ToString("N");
            string detail = DiagnosticRedaction.Body(fields.ToJsonString());
            foreach (Action<string, string, bool> writer in writers.GetInvocationList())
                try { writer(phase, detail, failed); } catch { }
        }
        catch { }
    }

    private sealed class Scope(string? previous) : IDisposable
    {
        public void Dispose() => Current.Value = previous;
    }
}
