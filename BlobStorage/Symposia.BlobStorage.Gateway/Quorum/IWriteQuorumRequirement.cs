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

/// <summary>
/// The flat <see cref="GatewayOptions.WriteQuorumCount"/> in bootstrap mode; in network mode the quorum of
/// a global (no-region) blob, the smallest in the #7 table.
/// </summary>
public sealed class ConfiguredWriteQuorum(IOptions<GatewayOptions> options) : IWriteQuorumRequirement
{
    public int MinimumEligibleNodes => options.Value.Redundancy.Mode == RedundancyMode.Network
        ? QuorumPolicy.Resolve([]).WriteQuorum
        : Math.Max(1, options.Value.WriteQuorumCount);
}
