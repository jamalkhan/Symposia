using Microsoft.Extensions.Logging;
using Symposia.Network.Node.Alerts;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Alerts;

/// <summary>One alert key's observed state plus what was last emailed for it.</summary>
public sealed class AlertRecord
{
    public string Key { get; set; } = "";

    /// <summary>Observed severity; null once cleared (kept until the clear is notified).</summary>
    public AlertSeverity? Severity { get; set; }

    public string Title { get; set; } = "";

    public string Detail { get; set; } = "";

    public DateTimeOffset RaisedAt { get; set; }

    public DateTimeOffset LastChangedAt { get; set; }

    /// <summary>Severity last emailed (null: none, or the clear was emailed).</summary>
    public AlertSeverity? NotifiedSeverity { get; set; }

    public DateTimeOffset? NotifiedAt { get; set; }

    public DateTimeOffset? LastReminderAt { get; set; }

    /// <summary>Restored from disk and not yet re-asserted by its feature since this start.</summary>
    public bool Stale { get; set; }
}

public sealed record ActiveAlert(string Key, AlertSeverity Severity, string Title, string Detail, DateTimeOffset RaisedAt, DateTimeOffset LastChangedAt);

/// <summary>
/// The host's Operator Alerts (issue #196): observed state per key, persisted to
/// alerts-state.json so a restart neither forgets an alert nor re-emails it.
/// <see cref="AlertNotifier"/> turns differences between observed and notified state into email.
/// </summary>
public sealed class OperatorAlerts : IOperatorAlertSink
{
    /// <summary>A restored alert nobody re-asserts within this long after start is cleared.</summary>
    public static readonly TimeSpan StaleGrace = TimeSpan.FromMinutes(5);

    private sealed class StateFile
    {
        public int Version { get; set; } = 1;

        public List<AlertRecord> Alerts { get; set; } = [];
    }

    private readonly string _path;
    private readonly ILogger _logger;
    private readonly TimeProvider _time;
    private readonly DateTimeOffset _startedAt;
    private readonly object _gate = new();
    private readonly Dictionary<string, AlertRecord> _alerts = new(StringComparer.Ordinal);

    public OperatorAlerts(string path, ILogger logger, TimeProvider? time = null)
    {
        _path = path;
        _logger = logger;
        _time = time ?? TimeProvider.System;
        _startedAt = _time.GetUtcNow();
        StateFile? file = null;
        try
        {
            file = SecureFile.Read<StateFile>(path);
        }
        catch (CorruptFileException ex)
        {
            // Alert state is a cache of what features re-assert; losing it costs at most a repeat email.
            logger.LogWarning("alerts: ignoring unreadable {Path}: {Message}", path, ex.Message);
        }

        foreach (var a in file?.Alerts ?? [])
        {
            a.Stale = a.Severity is not null;
            _alerts[a.Key] = a;
        }
    }

    public IReadOnlyList<ActiveAlert> Active
    {
        get
        {
            lock (_gate)
            {
                return _alerts.Values
                    .Where(a => a.Severity is not null)
                    .OrderByDescending(a => a.Severity).ThenBy(a => a.RaisedAt)
                    .Select(a => new ActiveAlert(a.Key, a.Severity!.Value, a.Title, a.Detail, a.RaisedAt, a.LastChangedAt))
                    .ToList();
            }
        }
    }

    public void Raise(string key, AlertSeverity severity, string title, string detail)
    {
        lock (_gate)
        {
            var now = _time.GetUtcNow();
            if (!_alerts.TryGetValue(key, out var a))
            {
                a = new AlertRecord { Key = key };
                _alerts[key] = a;
            }

            var changed = a.Severity != severity;
            if (!changed && a.Title == title && a.Detail == detail && !a.Stale)
            {
                return;
            }

            if (changed)
            {
                _logger.LogInformation("alerts: {Key} {From}→{To}", key, a.Severity?.ToString() ?? "none", severity);
                if (a.Severity is null)
                {
                    a.RaisedAt = now;
                }

                a.LastChangedAt = now;
            }

            a.Severity = severity;
            a.Title = title;
            a.Detail = detail;
            a.Stale = false;
            Save();
        }
    }

    public void Clear(string key)
    {
        lock (_gate)
        {
            if (!_alerts.TryGetValue(key, out var a) || a.Severity is null)
            {
                return;
            }

            _logger.LogInformation("alerts: {Key} {From}→none", key, a.Severity);
            a.Severity = null;
            a.Stale = false;
            a.LastChangedAt = _time.GetUtcNow();
            Save();
        }
    }

    /// <summary>Clears restored alerts nobody re-asserted within <see cref="StaleGrace"/> of start.</summary>
    internal void ExpireStale()
    {
        if (_time.GetUtcNow() - _startedAt < StaleGrace)
        {
            return;
        }

        List<string> stale;
        lock (_gate)
        {
            stale = _alerts.Values.Where(a => a.Stale).Select(a => a.Key).ToList();
        }

        foreach (var key in stale)
        {
            Clear(key);
        }
    }

    /// <summary>Runs <paramref name="update"/> on every record under the lock, then persists and drops settled clears.</summary>
    internal void Update(Action<AlertRecord> update)
    {
        lock (_gate)
        {
            foreach (var a in _alerts.Values)
            {
                update(a);
            }

            foreach (var key in _alerts.Values.Where(a => a.Severity is null && a.NotifiedSeverity is null).Select(a => a.Key).ToList())
            {
                _alerts.Remove(key);
            }

            Save();
        }
    }

    internal IReadOnlyList<AlertRecord> Snapshot()
    {
        lock (_gate)
        {
            return _alerts.Values.Select(a => a.Copy()).ToList();
        }
    }

    private void Save()
    {
        try
        {
            SecureFile.Write(_path, new StateFile { Alerts = [.._alerts.Values] }, ownerOnly: false);
        }
        catch (IOException ex)
        {
            _logger.LogWarning("alerts: could not save {Path}: {Message}", _path, ex.Message);
        }
    }
}

internal static class AlertRecordExtensions
{
    public static AlertRecord Copy(this AlertRecord a) => new()
    {
        Key = a.Key, Severity = a.Severity, Title = a.Title, Detail = a.Detail, RaisedAt = a.RaisedAt,
        LastChangedAt = a.LastChangedAt, NotifiedSeverity = a.NotifiedSeverity, NotifiedAt = a.NotifiedAt,
        LastReminderAt = a.LastReminderAt, Stale = a.Stale,
    };
}
