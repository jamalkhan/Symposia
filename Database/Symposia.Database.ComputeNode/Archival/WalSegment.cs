namespace Symposia.Database.ComputeNode.Archival;

/// <summary>A quorum-confirmed WAL segment awaiting or undergoing async upload to the tenant's Tier 1 bucket.</summary>
public sealed record WalSegment(string TimelineId, long Lsn, long SequenceNumber, int SizeBytes);
