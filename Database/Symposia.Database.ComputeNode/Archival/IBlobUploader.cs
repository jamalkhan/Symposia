namespace Symposia.Database.ComputeNode.Archival;

/// <summary>
/// Uploads a confirmed WAL segment to a tenant's dedicated Tier 1 bucket using the tenant's
/// narrow-scoped, provisioning-time credential -- the same credential-issuance pattern the
/// pageserver already uses for page uploads (Arch: no new credential system). Kept as an
/// interface so the archiver's retry/backoff/watermark logic can be tested without a real
/// blob backend, mirroring <c>IProcessLauncher</c>'s role for <c>ManagedProcess</c>.
/// </summary>
public interface IBlobUploader
{
    Task<UploadOutcome> UploadAsync(string tenantDatabaseId, WalSegment segment, CancellationToken cancellationToken);
}
