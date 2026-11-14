using Microsoft.Extensions.Options;

namespace Symposia.BlobStorage.Gateway.Quorum;

/// <summary>
/// How many writable nodes a write needs before the gateway can take traffic (#1 FR-24's
/// <c>insufficient_eligible_nodes</c>): the quorum of the least demanding placement this gateway serves.
/// </summary>
public interface IWriteQuorumRequirement
{
    int MinimumEligibleNodes { get; }
}

/// <summary>The flat <see cref="GatewayOptions.WriteQuorumCount"/>.</summary>
public sealed class ConfiguredWriteQuorum(IOptions<GatewayOptions> options) : IWriteQuorumRequirement
{
    public int MinimumEligibleNodes => Math.Max(1, options.Value.WriteQuorumCount);
}
