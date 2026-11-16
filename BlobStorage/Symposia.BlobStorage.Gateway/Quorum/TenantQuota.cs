using Microsoft.Extensions.Options;
using Symposia.BlobStorage.Gateway.Metadata;
using Symposia.BlobStorage.Gateway.S3;

namespace Symposia.BlobStorage.Gateway.Quorum;

/// <summary>
/// Refuses a write that would take its tenant past quota (#1 FR-2), before placement or any node
/// contact, so an over-quota upload costs nothing. Requirements/BlobStorage/quota-management.md owns the
/// quota model; this is the gateway's enforcement hook, fed by <see cref="GatewayOptions.TenantQuotaBytes"/>.
/// </summary>
public sealed class TenantQuota(GatewayMetadataStore store, IOptions<GatewayOptions> options)
{
    /// <summary>Throws a 507 rejection when <paramref name="incomingBytes"/> (or, unknown, any write) won't fit.</summary>
    public void EnsureRoom(string tenantId, long? incomingBytes)
    {
        if (!options.Value.TenantQuotaBytes.TryGetValue(tenantId, out var quota))
        {
            return;
        }

        var used = store.TenantUsageBytes(tenantId);
        if (used + Math.Max(0, incomingBytes ?? 0) > quota || used >= quota)
        {
            throw new S3RequestRejectedException(StatusCodes.Status507InsufficientStorage, "QuotaExceeded",
                $"This write would exceed the tenant's storage quota of {quota} bytes ({used} in use).");
        }
    }
}
