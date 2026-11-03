using System.Text;
using Microsoft.Extensions.Logging;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Diagnostics;

/// <summary>The three independently configured log streams.</summary>
public enum LogService
{
    Node,
    Storage,
    Compute,
}

/// <summary>
/// Owns one rolling file writer per <see cref="LogService"/> and reads each service's
/// minimum level from the current settings on every call, so a level change in the
/// admin UI takes effect immediately without restarting anything.
/// </summary>
public sealed class HostLogging(HostPaths paths, Func<LoggingSettings> settings) : IDisposable
{
    private readonly Dictionary<LogService, RollingFileWriter> _writers = Enum.GetValues<LogService>()
        .ToDictionary(s => s, s => new RollingFileWriter(paths.Logs(s.ToString().ToLowerInvariant()), s.ToString().ToLowerInvariant()));

    public static LogService ServiceFor(string component) => component switch
    {
        "storage" => LogService.Storage,
        "compute" => LogService.Compute,
        _ => LogService.Node,
    };

    public LogLevel MinimumLevel(LogService service)
    {
        var current = settings();
        return service switch
        {
            LogService.Storage => current.Storage,
            LogService.Compute => current.Compute,
            _ => current.Node,
        };
    }

    public RollingFileWriter Writer(LogService service) => _writers[service];

    public void Dispose()
    {
        foreach (var writer in _writers.Values)
        {
            writer.Dispose();
        }
    }
}

/// <summary>
/// Logger provider every hosted component uses: tags each line with its component,
/// filters by its service's configured level, and writes it to that service's log
/// files, the console, and the diagnostics page buffer.
/// </summary>
public sealed class ComponentLoggerProvider(string component, LogBuffer buffer, HostLogging logging) : ILoggerProvider
{
    private static readonly object ConsoleGate = new();

    private readonly string _component = component;
    private readonly LogBuffer _buffer = buffer;
    private readonly HostLogging _logging = logging;
    private readonly LogService _service = HostLogging.ServiceFor(component);

    public ILogger CreateLogger(string categoryName) => new ComponentLogger(this, categoryName);

    public void Dispose()
    {
    }

    private sealed class ComponentLogger(ComponentLoggerProvider owner, string category) : ILogger
    {
        // ASP.NET Core / runtime categories are very chatty at Information; keep them at
        // Warning unless the operator has asked for Debug or Trace on this service.
        private readonly bool _framework =
            category.StartsWith("Microsoft.", StringComparison.Ordinal) || category.StartsWith("System.", StringComparison.Ordinal);

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel == LogLevel.None)
            {
                return false;
            }

            var minimum = owner._logging.MinimumLevel(owner._service);
            if (_framework && minimum > LogLevel.Debug)
            {
                minimum = (LogLevel)Math.Max((int)minimum, (int)LogLevel.Warning);
            }

            return logLevel >= minimum;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            if (!IsEnabled(logLevel))
            {
                return;
            }

            var now = DateTimeOffset.Now;
            var message = formatter(state, exception);
            var level = logLevel.ToString();

            // Files get the full category and stack trace; console and UI get the short form.
            var file = new StringBuilder()
                .Append(now.ToString("yyyy-MM-dd HH:mm:ss.fff zzz")).Append(' ')
                .Append(Short(logLevel)).Append(" [").Append(owner._component).Append("] ")
                .Append(category).Append(": ").Append(message);
            if (exception is not null)
            {
                file.AppendLine().Append(exception);
                message += $" ({exception.GetType().Name}: {exception.Message})";
            }

            owner._logging.Writer(owner._service).Write(file.ToString());
            owner._buffer.Add(new LogLine(now, owner._component, level, message));
            lock (ConsoleGate)
            {
                Console.WriteLine($"[{owner._component}] {level}: {message}");
            }
        }

        private static string Short(LogLevel level) => level switch
        {
            LogLevel.Trace => "TRC",
            LogLevel.Debug => "DBG",
            LogLevel.Information => "INF",
            LogLevel.Warning => "WRN",
            LogLevel.Error => "ERR",
            _ => "CRT",
        };
    }
}

/// <summary>
/// Appends lines to &lt;dir&gt;/&lt;prefix&gt;-yyyyMMdd.log, starting a new file each day or
/// when the current one passes <see cref="MaxFileBytes"/>, and keeps the newest
/// <see cref="RetainedFiles"/> files.
/// </summary>
public sealed class RollingFileWriter(string directory, string prefix) : IDisposable
{
    public const long MaxFileBytes = 10 * 1024 * 1024;
    public const int RetainedFiles = 14;

    private readonly object _gate = new();
    private StreamWriter? _writer;
    private string? _day;
    private int _part;
    private bool _failed;

    public void Write(string line)
    {
        lock (_gate)
        {
            if (_failed)
            {
                return;
            }

            try
            {
                var day = DateTime.Now.ToString("yyyyMMdd");
                if (_writer is null || day != _day || _writer.BaseStream.Length >= MaxFileBytes)
                {
                    Open(day);
                }

                _writer!.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // Never let logging take the node down; say so once and stop trying.
                _failed = true;
                Console.Error.WriteLine($"[logging] Could not write {prefix} log files in {directory}: {ex.Message}");
            }
        }
    }

    private void Open(string day)
    {
        _writer?.Dispose();
        Directory.CreateDirectory(directory);
        _part = day == _day ? _part + 1 : 0;
        _day = day;
        string path;
        do
        {
            path = Path.Combine(directory, _part == 0 ? $"{prefix}-{day}.log" : $"{prefix}-{day}.{_part}.log");
        }
        while (File.Exists(path) && new FileInfo(path).Length >= MaxFileBytes && ++_part > 0);

        _writer = new StreamWriter(new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        Prune();
    }

    private void Prune()
    {
        foreach (var old in new DirectoryInfo(directory).GetFiles($"{prefix}-*.log")
                     .OrderByDescending(f => f.LastWriteTimeUtc).Skip(RetainedFiles))
        {
            try
            {
                old.Delete();
            }
            catch (IOException)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _writer?.Dispose();
            _writer = null;
        }
    }
}
