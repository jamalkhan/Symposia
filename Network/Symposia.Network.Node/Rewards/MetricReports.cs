using System.Numerics;
using Nethereum.ABI;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer;
using Nethereum.Util;
using Symposia.Network.Node.Staking;

namespace Symposia.Network.Node.Rewards;

/// <summary>
/// One service's per-epoch factor readings for <c>RewardDistributor.submitServiceFactorMetrics</c> (#221).
/// <see cref="Values"/> follows ScoringWeights' factor order for <see cref="Service"/>.
/// </summary>
public sealed record MetricReport(string Distributor, string Node, byte Service, ulong Epoch, IReadOnlyList<BigInteger> Values, uint HeartbeatBps);

/// <summary>A signature over <see cref="MetricReportDigest.Hash"/> and the address that made it.</summary>
public sealed record MetricAttestation(string Signature, string Signer);

/// <summary>
/// Signs metric reports as <c>reward.metric.signer</c> (#221). LocalDev self-signs
/// (<see cref="LocalDevSelfSigner"/>); the foundation Network Monitor (#225) is the production attestor.
/// </summary>
public interface IMetricAttestor
{
    string Name { get; }

    Task<MetricAttestation> AttestAsync(MetricReport report, CancellationToken ct);
}

/// <summary>
/// The digest <c>submitServiceFactorMetrics</c> recovers the signer from:
/// <c>keccak256(abi.encode(distributor, node, uint8(service), epoch, values, heartbeatBps))</c>, signed with
/// the eth-signed-message prefix (<c>MessageHashUtils.toEthSignedMessageHash</c>).
/// </summary>
public static class MetricReportDigest
{
    public static byte[] Hash(MetricReport r) => Sha3Keccack.Current.CalculateHash(new ABIEncode().GetABIEncoded(
        new ABIValue("address", r.Distributor),
        new ABIValue("address", r.Node),
        new ABIValue("uint8", r.Service),
        new ABIValue("uint256", new BigInteger(r.Epoch)),
        new ABIValue("uint256[]", r.Values.ToArray()),
        new ABIValue("uint256", new BigInteger(r.HeartbeatBps))));

    /// <summary>The address that signed <paramref name="report"/>, or "" for a malformed signature.</summary>
    public static string Recover(MetricReport report, string signature)
    {
        try
        {
            return new EthereumMessageSigner().EcRecover(Hash(report), signature);
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            return "";
        }
    }
}

/// <summary>Thrown when a metric signer is configured where it must not run.</summary>
public sealed class MetricSignerConfigurationException(string message) : Exception(message);

/// <summary>
/// The gateway signs its own report with the LocalDev deployer key, which DeployRewards makes
/// <c>roles.metricSigner</c> (#221 FR3.5). Self-reporting is trivially gameable, so this refuses any chain
/// but LocalDev's (FR3.6): the chain ID comes from the RPC, not only from settings.
/// </summary>
public sealed class LocalDevSelfSigner : IMetricAttestor
{
    private readonly EthECKey _key;

    public LocalDevSelfSigner(string privateKey, ulong chainId)
    {
        if (chainId != DevFunding.LocalDevChainId)
        {
            throw new MetricSignerConfigurationException(
                $"The LocalDev metric self-signer only runs on LocalDev (chain {DevFunding.LocalDevChainId}); this host is on chain {chainId}.");
        }

        _key = new EthECKey(privateKey);
    }

    public string Name => "localdev-self-signer";

    public string Address => _key.GetPublicAddress();

    public Task<MetricAttestation> AttestAsync(MetricReport report, CancellationToken ct) =>
        Task.FromResult(new MetricAttestation(new EthereumMessageSigner().Sign(MetricReportDigest.Hash(report), _key), Address));
}

/// <summary>The Blockchain Gateway's service-metrics relay (#221): it pays the gas, the contract checks the signature.</summary>
public static class ServiceMetricsRelayWire
{
    public const string Path = "/v1/relay/service-metrics";

    /// <summary><paramref name="Values"/> are decimal strings: uint256 doesn't fit a JSON number.</summary>
    public sealed record Request(string Node, byte Service, ulong Epoch, IReadOnlyList<string> Values, uint HeartbeatBps, string Signature)
    {
        public static Request From(MetricReport r, MetricAttestation a) =>
            new(r.Node, r.Service, r.Epoch, [.. r.Values.Select(v => v.ToString())], r.HeartbeatBps, a.Signature.EnsureHexPrefix());
    }

    public sealed record Response(string? TransactionHash, string? Error);
}
