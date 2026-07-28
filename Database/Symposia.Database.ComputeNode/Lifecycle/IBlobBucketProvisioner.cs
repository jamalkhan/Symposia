namespace Symposia.Database.ComputeNode.Lifecycle;

/// <summary>
/// Provisions the Tier 1 blob bucket for page storage/WAL archival (FR2) and triggers the
/// standard blob soft-delete mechanism on database deletion (FR12/13). This issue is a consumer
/// of that mechanism, not its owner -- out of scope per the spec.
/// </summary>
public interface IBlobBucketProvisioner
{
    Task<string> ProvisionBucketAsync(string databaseId, CancellationToken cancellationToken);

    Task SoftDeleteBucketAsync(string bucketId, CancellationToken cancellationToken);
}
