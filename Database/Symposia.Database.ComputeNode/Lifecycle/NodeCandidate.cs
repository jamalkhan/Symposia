namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>A compute node candidate for placement, as reported by #90's capacity declarations.</summary>
public sealed record NodeCandidate(string NodeId, string Region, int Tier, int AvailableCapacity, string Host, int Port);
