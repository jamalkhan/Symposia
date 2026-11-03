namespace Symposia.Network.Node;

/// <summary>
/// Raised at startup when a previously initialized node cannot load its
/// identity. The node must not start under a new identity silently, since
/// that would orphan its registry entry and history (#62 FR17, #129 FR-A3).
/// </summary>
public sealed class NodeIdentityException(string message, Exception? inner = null) : Exception(message, inner);
