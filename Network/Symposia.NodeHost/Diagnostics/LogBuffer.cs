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

/// <summary>
/// Logger provider every hosted component uses: tags each line with its
/// component, writes it to the console, and keeps it in the <see cref="LogBuffer"/>.
/// </summary>
public sealed class ComponentLoggerProvider(string component, LogBuffer buffer) : ILoggerProvider
{
    private static readonly object ConsoleGate = new();

    public ILogger CreateLogger(string categoryName) => new ComponentLogger(component, categoryName, buffer);

    public void Dispose()
    {
    }

    private sealed class ComponentLogger(string component, string category, LogBuffer buffer) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) =>
            logLevel >= (category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal)
                ? LogLevel.Warning
                : LogLevel.Information);

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var message = formatter(state, exception);
            if (exception is not null)
            {
                message += $" ({exception.GetType().Name}: {exception.Message})";
            }

            var level = logLevel.ToString();
            buffer.Add(new LogLine(DateTimeOffset.UtcNow, component, level, message));
            lock (ConsoleGate)
            {
                Console.WriteLine($"[{component}] {level}: {message}");
            }
        }
    }
}
