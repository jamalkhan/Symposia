namespace Symposia.BlobStorage.Gateway.Quota;

public enum QuotaAlertSeverity
{
    Info,
    Warning,
    Critical,
}

/// <summary>A quota threshold crossed (#15 FR7): which account or bucket, which threshold, and the utilization that crossed it.</summary>
public sealed record QuotaAlert(string TenantId, string Scope, string? Bucket, int ThresholdPct, QuotaAlertSeverity Severity, double UtilizationPct,
    long UsedBytes, long LimitBytes, DateTimeOffset At);

/// <summary>
/// Where quota alerts go. Tenant webhook and email delivery belong to tenant observability
/// (Requirements/BlobStorage/tenant-observability.md), which isn't built yet; it replaces the logging sink.
/// Called after the write has committed, and must not block.
/// </summary>
public interface IQuotaAlertSink
{
    void Notify(QuotaAlert alert);
}

public sealed class LoggingQuotaAlertSink(ILogger<LoggingQuotaAlertSink> logger) : IQuotaAlertSink
{
    public void Notify(QuotaAlert alert) =>
        logger.Log(alert.Severity switch { QuotaAlertSeverity.Critical => LogLevel.Error, QuotaAlertSeverity.Warning => LogLevel.Warning, _ => LogLevel.Information },
            "Quota alert {Severity}: {Scope} {Id} of tenant {Tenant} crossed {Threshold}% ({Utilization}% of {Limit} bytes).",
            alert.Severity.ToString().ToUpperInvariant(), alert.Scope, alert.Bucket ?? alert.TenantId, alert.TenantId, alert.ThresholdPct,
            alert.UtilizationPct, alert.LimitBytes);
}

/// <summary>
/// Returns the headroom of reservations whose write vanished without ending them: a crashed gateway, a hung client
/// (#15 AC4, QA-22). Live writes renew their lease, so only abandoned ones lapse.
/// </summary>
public sealed class ReservationSweeper(Metadata.GatewayMetadataStore store, Microsoft.Extensions.Options.IOptions<GatewayOptions> options,
    ILogger<ReservationSweeper> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, options.Value.Quota.SweepIntervalSeconds));
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                if (store.SweepReservations() is var swept and > 0)
                {
                    logger.LogInformation("Released {Count} lapsed quota reservation(s).", swept);
                }
            }
            catch (Microsoft.Data.Sqlite.SqliteException ex)
            {
                logger.LogWarning(ex, "Sweeping quota reservations failed; retrying.");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
