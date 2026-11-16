namespace Symposia.BlobStorage.Gateway.Health;

/// <summary>
/// Whether this gateway is draining for shutdown (#1 FR-28). Set when the host begins stopping, before
/// Kestrel closes its listeners, so <c>/ready</c> answers 503 and the load balancer stops routing new
/// connections here while in-flight requests finish.
/// </summary>
public sealed class ShutdownState
{
    private volatile bool _draining;

    public bool IsDraining => _draining;

    public void BeginDrain() => _draining = true;
}
