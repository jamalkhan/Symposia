using Microsoft.Extensions.Logging;

namespace Symposia.NodeHost.Diagnostics;

public sealed record LogLine(DateTimeOffset Time, string Component, string Level, string Message);

/// <summary>Last N log lines from every component, shown on the diagnostics page.</summary>
public sealed class LogBuffer(int capacity = 500)
{
    private readonly Queue<LogLine> _lines = new();
    private readonly object _gate = new();

    public void Add(LogLine line)
    {
        lock (_gate)
        {
            _lines.Enqueue(line);
            while (_lines.Count > capacity)
            {
                _lines.Dequeue();
            }
        }
    }

    public IReadOnlyList<LogLine> Tail(int count)
    {
        lock (_gate)
        {
            return _lines.Skip(Math.Max(0, _lines.Count - count)).ToList();
        }
    }
}
