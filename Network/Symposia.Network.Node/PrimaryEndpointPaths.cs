namespace Symposia.Network.Node;

/// <summary>
/// The public paths a node's primary endpoint (the one the registry holds) serves to anyone: the peer handshake, the
/// node health summary and the signed service directory (#228). One list, so a role that guards everything else
/// (compute's off-box filter, the gateway's SigV4) lets exactly these through.
/// </summary>
public static class PrimaryEndpointPaths
{
    public const string Handshake = "/v1/peer/handshake";

    public const string Health = "/v1/node/health";

    public const string Directory = Discovery.ServiceDirectory.Path;

    /// <summary>Exact matches only: a prefix would open the gateway's tenant /v1 API.</summary>
    public static bool Contains(string? path) => path is Handshake or Health or Directory;
}
