namespace Symposia.Database.ComputeNode.Proxy;

/// <summary>A single routable backend target (primary or replica) from the #93 routing table.</summary>
public sealed record ComputeEndpoint(string NodeId, string Host, int Port);
