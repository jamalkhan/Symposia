using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Symposia.Security.TlsPinning;

namespace Symposia.BlobStorage.Gateway.Nodes;

/// <summary>Where a node connection reports a refused TLS handshake (#252).</summary>
public interface ITlsRefusalSink
{
    void Refused(string nodeId, string url, TlsPinResult reason, string? presentedFingerprint);
}

/// <summary>
/// Counts refused storage-node handshakes by reason (<c>storage_node_tls_refusals_total</c>; no node tag, so the
/// series count stays fixed as the registry grows) and logs each (node, reason) at most every five minutes: a refused
/// node is re-dialled by every probe and would otherwise flood the log.
/// </summary>
public sealed class NodeTlsRefusals(ILogger<NodeTlsRefusals> logger, IMeterFactory meters) : ITlsRefusalSink
{
    private static readonly TimeSpan LogEvery = TimeSpan.FromMinutes(5);

    private readonly Counter<long> _refusals = meters.Create("Symposia.BlobStorage.Gateway.Nodes")
        .CreateCounter<long>("storage_node_tls_refusals_total");

    private readonly ConcurrentDictionary<(string, TlsPinResult), DateTimeOffset> _lastLogged = new();

    public void Refused(string nodeId, string url, TlsPinResult reason, string? presentedFingerprint)
    {
        _refusals.Add(1, new KeyValuePair<string, object?>("reason", reason.ToTag()));
        var now = DateTimeOffset.UtcNow;
        var key = (nodeId, reason);
        // Atomic claim of the log slot: two probes refused at once log once, not twice (review: Frank #2).
        var logged = false;
        _lastLogged.AddOrUpdate(key, _ => { logged = true; return now; }, (_, last) =>
        {
            if (now - last < LogEvery) return last;
            logged = true;
            return now;
        });
        if (!logged)
        {
            return;
        }

        if (_lastLogged.Count > 4096)
        {
            // Nodes that left the registry: drop anything outside the window rather than grow forever.
            foreach (var stale in _lastLogged.Where(e => now - e.Value >= LogEvery).Select(e => e.Key).ToList())
            {
                _lastLogged.TryRemove(stale, out _);
            }
        }

        logger.LogWarning("Refused TLS to storage node {Node} at {Url}: {Reason} (presented certificate {Fingerprint}).",
            nodeId, url, reason.ToTag(), presentedFingerprint ?? "none");
    }
}
