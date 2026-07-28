namespace Symposia.Database.ComputeNode.Proxy;

public enum ConnectionOutcome
{
    Routed,
    AuthRejected,
    ConnectionLimitExceeded,
    NoRouteAvailable,
}

/// <summary>Result of routing a connection request through auth, admission, and routing-table lookup.</summary>
public sealed record ConnectionDecision(ConnectionOutcome Outcome, ComputeEndpoint? Endpoint, string? Reason)
{
    public static ConnectionDecision Routed(ComputeEndpoint endpoint) => new(ConnectionOutcome.Routed, endpoint, null);
    public static ConnectionDecision AuthRejected() => new(ConnectionOutcome.AuthRejected, null, "Invalid or revoked credential.");
    public static ConnectionDecision LimitExceeded() => new(ConnectionOutcome.ConnectionLimitExceeded, null, "Database max connection ceiling reached.");
    public static ConnectionDecision NoRoute(string reason) => new(ConnectionOutcome.NoRouteAvailable, null, reason);
}
