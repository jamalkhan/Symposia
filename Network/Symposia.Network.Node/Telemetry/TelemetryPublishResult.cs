namespace Symposia.Network.Node.Telemetry;

/// <summary>Why gossip refused a telemetry item (#183). <see cref="None"/> means it was accepted.</summary>
public enum TelemetryRejection
{
    None,
    ClockSkew,
    EpochOutOfRange,
    InvalidSignature,
    Duplicate,
    ReplayLimit,
}

/// <summary>
/// Outcome of publishing a telemetry item (#183). Deliberately not convertible to <c>bool</c>, so a
/// caller can't silently drop the reason; <see cref="Detail"/> is only built on rejection.
/// </summary>
public readonly record struct TelemetryPublishResult(TelemetryRejection Rejection, string? Detail = null)
{
    public static TelemetryPublishResult Ok => default;

    public bool Accepted => Rejection == TelemetryRejection.None;

    public static TelemetryPublishResult Reject(TelemetryRejection rejection, string? detail = null) => new(rejection, detail);

    /// <summary>Operator-facing text, e.g. for the Diagnostics page.</summary>
    public string Describe() => Rejection switch
    {
        TelemetryRejection.None => "accepted",
        TelemetryRejection.ClockSkew => $"rejected by gossip admission: {Detail ?? "claimed time is outside the allowed clock skew"}",
        TelemetryRejection.EpochOutOfRange => $"rejected by gossip admission: {Detail ?? "wrong epoch"}",
        TelemetryRejection.InvalidSignature => "rejected by gossip: signature does not verify for this chain and registry",
        TelemetryRejection.Duplicate => "rejected by gossip: an identical copy was already seen",
        TelemetryRejection.ReplayLimit => "rejected by gossip: this epoch and slot already hold the maximum two copies",
        _ => $"rejected by gossip ({Rejection})",
    };
}
