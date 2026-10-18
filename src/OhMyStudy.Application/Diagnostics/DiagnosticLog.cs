using System.Collections.Concurrent;

namespace OhMyStudy.Application.Diagnostics;

public sealed record DiagnosticEntry(DateTimeOffset Time, string Event, string CorrelationId, string Outcome, long ElapsedMilliseconds);

/// <summary>Only identifiers, timings and fixed outcome codes are accepted, never exception messages or payloads.</summary>
public sealed class DiagnosticLog
{
    private readonly ConcurrentQueue<DiagnosticEntry> _entries = new();
    private readonly int _capacity;
    public DiagnosticLog(int capacity = 256)
    {
        if (capacity is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(capacity));
        _capacity = capacity;
    }
    public void Write(string eventCode, string correlationId, string outcomeCode, long elapsedMilliseconds = 0)
    {
        if (!IsCode(eventCode) || !IsCode(correlationId) || !IsCode(outcomeCode) || elapsedMilliseconds < 0)
            throw new ArgumentException("诊断只接受标识和固定状态码。");
        _entries.Enqueue(new(DateTimeOffset.UtcNow, eventCode, correlationId, outcomeCode, elapsedMilliseconds));
        while (_entries.Count > _capacity) _entries.TryDequeue(out _);
    }
    public IReadOnlyList<DiagnosticEntry> Snapshot() => _entries.ToArray();
    private static bool IsCode(string value) => value.Length is > 0 and <= 128 && value.All(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' or '.');
}
