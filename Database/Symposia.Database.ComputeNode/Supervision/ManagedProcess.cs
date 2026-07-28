using Microsoft.Extensions.Logging;

namespace Symposia.Database.ComputeNode.Supervision;

/// <summary>
/// Supervises the lifecycle of a single OS child process (Postgres, pageserver, or safekeeper):
/// starts it, detects crashes, and restarts with bounded exponential backoff. A process that
/// crash-loops (more than <see cref="ComputeNodeOptions.MaxRestartAttempts"/> restarts within
/// <see cref="ComputeNodeOptions.CrashLoopWindowSeconds"/>) is marked <see cref="ProcessState.Unhealthy"/>
/// instead of retried again (spec FR4).
/// </summary>
public sealed class ManagedProcess
{
    private readonly string _name;
    private readonly string _executablePath;
    private readonly string _arguments;
    private readonly IReadOnlyDictionary<string, string>? _environment;
    private readonly IProcessLauncher _launcher;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly int _maxRestartAttempts;
    private readonly TimeSpan _crashLoopWindow;
    private readonly TimeSpan _backoffBase;

    private readonly List<DateTimeOffset> _recentRestarts = [];
    private readonly Lock _gate = new();

    private ILaunchedProcess? _process;

    public ManagedProcess(
        string name,
        string executablePath,
        string arguments,
        IProcessLauncher launcher,
        ComputeNodeOptions options,
        ILogger logger,
        IReadOnlyDictionary<string, string>? environment = null,
        TimeProvider? timeProvider = null)
    {
        _name = name;
        _executablePath = executablePath;
        _arguments = arguments;
        _environment = environment;
        _launcher = launcher;
        _logger = logger;
        _maxRestartAttempts = options.MaxRestartAttempts;
        _crashLoopWindow = TimeSpan.FromSeconds(options.CrashLoopWindowSeconds);
        _backoffBase = TimeSpan.FromSeconds(options.RestartBackoffBaseSeconds);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public string Name => _name;

    public ProcessState State { get; private set; } = ProcessState.Stopped;

    /// <summary>Number of restart attempts recorded so far, exposed for tests/observability.</summary>
    public int RestartAttemptCount { get; private set; }

    public void Start()
    {
        lock (_gate)
        {
            State = ProcessState.Starting;
            _process = _launcher.Start(_executablePath, _arguments, _environment);
            _process.Exited += OnExited;
            State = ProcessState.Running;
            _logger.LogInformation("{Process} started.", _name);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            if (_process is not null)
            {
                _process.Exited -= OnExited;
                _process.Kill();
                _process.Dispose();
                _process = null;
            }
            State = ProcessState.Stopped;
        }
    }

    private void OnExited(object? sender, EventArgs e)
    {
        lock (_gate)
        {
            if (State == ProcessState.Stopped)
                return; // intentional stop, not a crash

            _logger.LogWarning("{Process} exited unexpectedly.", _name);

            var now = _timeProvider.GetUtcNow();
            _recentRestarts.RemoveAll(t => now - t > _crashLoopWindow);
            _recentRestarts.Add(now);
            RestartAttemptCount++;

            if (_recentRestarts.Count > _maxRestartAttempts)
            {
                State = ProcessState.Unhealthy;
                _logger.LogError(
                    "{Process} crash-looped ({Count} restarts within {Window}); marking unhealthy instead of restarting.",
                    _name, _recentRestarts.Count, _crashLoopWindow);
                return;
            }

            State = ProcessState.Restarting;
            var backoff = TimeSpan.FromSeconds(_backoffBase.TotalSeconds * Math.Pow(2, _recentRestarts.Count - 1));
            _logger.LogInformation("{Process} restarting in {Backoff}.", _name, backoff);

            _ = RestartAfterDelayAsync(backoff);
        }
    }

    private async Task RestartAfterDelayAsync(TimeSpan delay)
    {
        await Task.Delay(delay, _timeProvider);

        lock (_gate)
        {
            if (State != ProcessState.Restarting)
                return; // stopped or already handled while we were waiting

            _process = _launcher.Start(_executablePath, _arguments, _environment);
            _process.Exited += OnExited;
            State = ProcessState.Running;
            _logger.LogInformation("{Process} restarted.", _name);
        }
    }
}
