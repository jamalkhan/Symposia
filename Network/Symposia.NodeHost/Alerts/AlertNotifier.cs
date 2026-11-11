using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Symposia.Network.Node.Alerts;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Alerts;

/// <summary>
/// Emails Operator Alert state changes (issue #196). Every tick it diffs each key's observed
/// severity against the last one emailed: a change that has held for <see cref="Damping"/> is
/// sent (raised, escalated, de-escalated, resolved), and an Error still standing gets one reminder
/// per <see cref="ReminderEvery"/>. Sends retry at 1, 5 and 15 minutes, then are dropped; a retry
/// whose state has since moved on is dropped too, because the newer state has its own email.
/// </summary>
public sealed class AlertNotifier(
    OperatorAlerts alerts,
    Func<HostSettings> settings,
    SecretStore secrets,
    IAlertMailTransport transport,
    string nodeAddress,
    Func<string?> uiUrl,
    ILogger logger,
    TimeProvider? time = null) : BackgroundService
{
    public static readonly TimeSpan Damping = TimeSpan.FromSeconds(60);
    public static readonly TimeSpan ReminderEvery = TimeSpan.FromHours(24);
    public static readonly TimeSpan[] RetryAfter = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15)];

    private readonly TimeProvider _time = time ?? TimeProvider.System;
    private readonly List<Pending> _pending = [];

    private sealed record Pending(string Key, AlertSeverity? State, AlertMailMessage Message)
    {
        public int Attempts { get; set; }

        public DateTimeOffset Due { get; set; }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(5), _time);
        do
        {
            try
            {
                await TickAsync(stoppingToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "alerts: notifier tick failed");
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task TickAsync(CancellationToken ct)
    {
        alerts.ExpireStale();
        var now = _time.GetUtcNow();
        var config = settings().Alerts;
        var emailing = !string.IsNullOrEmpty(config.Email);

        alerts.Update(a =>
        {
            if (a.Severity != a.NotifiedSeverity)
            {
                if (now - a.LastChangedAt < Damping)
                {
                    return;
                }

                var kind = a.Severity is null ? "RESOLVED" : a.Severity.Value.ToString().ToUpperInvariant();
                if (emailing)
                {
                    Enqueue(a, kind, now, config);
                }

                a.NotifiedSeverity = a.Severity;
                a.NotifiedAt = now;
                a.LastReminderAt = null;
            }
            else if (a.Severity == AlertSeverity.Error && now - (a.LastReminderAt ?? a.NotifiedAt ?? now) >= ReminderEvery)
            {
                if (emailing)
                {
                    Enqueue(a, "ERROR (still active)", now, config);
                }

                a.LastReminderAt = now;
            }
        });

        await SendDueAsync(now, config, ct);
    }

    private void Enqueue(AlertRecord a, string kind, DateTimeOffset now, AlertSettings config)
    {
        // An older queued message for this key is superseded by the newer state.
        _pending.RemoveAll(p => p.Key == a.Key);
        var url = uiUrl();
        var body = $"""
            {(a.Severity is null ? $"Resolved: {a.Title}" : a.Title)}

            {a.Detail}

            Node:     {nodeAddress}
            Host:     {Environment.MachineName}
            Time:     {now.UtcDateTime:yyyy-MM-dd HH:mm:ss} UTC
            Alert:    {a.Key}
            {(url is null ? "" : $"Node Host: {url}")}
            """;
        var message = new AlertMailMessage(
            config.Email,
            string.IsNullOrEmpty(config.From) ? config.Email : config.From,
            $"[Symposia {AlertMail.ShortAddress(nodeAddress)}] {kind}: {a.Title}",
            body.TrimEnd() + "\n");
        _pending.Add(new Pending(a.Key, a.Severity, message) { Due = now });
    }

    private async Task SendDueAsync(DateTimeOffset now, AlertSettings config, CancellationToken ct)
    {
        if (_pending.Count == 0)
        {
            return;
        }

        var current = alerts.Snapshot().ToDictionary(a => a.Key);
        var password = secrets.SmtpPassword;
        foreach (var p in _pending.Where(p => p.Due <= now).ToList())
        {
            var state = current.TryGetValue(p.Key, out var a) ? a.NotifiedSeverity : null;
            if (p.Attempts > 0 && state != p.State)
            {
                logger.LogInformation("alerts: dropping stale retry for {Key}", p.Key);
                _pending.Remove(p);
                continue;
            }

            try
            {
                await transport.SendAsync(p.Message, config, password, ct);
                logger.LogInformation("alerts: emailed {Subject} to {To}", p.Message.Subject, p.Message.To);
                _pending.Remove(p);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                var error = AlertMail.Scrub(ex.Message, password);
                if (p.Attempts >= RetryAfter.Length)
                {
                    logger.LogWarning("alerts: giving up on {Subject}: {Error}", p.Message.Subject, error);
                    _pending.Remove(p);
                    continue;
                }

                p.Due = now + RetryAfter[p.Attempts];
                p.Attempts++;
                logger.LogWarning("alerts: send failed ({Error}); retrying {Subject} at {Due:HH:mm:ss}", error, p.Message.Subject, p.Due);
            }
        }
    }
}
