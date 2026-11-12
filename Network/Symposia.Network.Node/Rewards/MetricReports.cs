using System.Numerics;
using Nethereum.ABI;
using Nethereum.Hex.HexConvertors.Extensions;
using Nethereum.Signer;
using Nethereum.Util;

namespace Symposia.Network.Node.Rewards;

/// <summary>
/// One service's per-epoch factor readings for <c>RewardDistributor.submitServiceFactorMetrics</c> (#221).
/// <see cref="Values"/> follows ScoringWeights' factor order for <see cref="Service"/>.
/// </summary>
public sealed record MetricReport(string Distributor, string Node, byte Service, ulong Epoch, IReadOnlyList<BigInteger> Values, uint HeartbeatBps);

/// <summary>A signature over <see cref="MetricReportDigest.Hash"/> and the address that made it.</summary>
public sealed record MetricAttestation(string Signature, string Signer);

/// <summary>
/// Signs metric reports as <c>reward.metric.signer</c>: the Verifier role (#225), run by the foundation for MVP. A
/// gateway never signs its own report.
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

/// <summary>The metric-signer key can't be used: missing, malformed, or readable by others.</summary>
public sealed class MetricSignerKeyException(string message) : Exception(message);

/// <summary>
/// Signs metric reports with a key held in a file (#225): the Verifier role's <c>reward.metric.signer</c>. The key
/// file must be owner-only, like the node key; the key is never logged or exposed.
/// </summary>
public sealed class LocalKeyMetricAttestor : IMetricAttestor
{
    private readonly EthECKey _key;

    public LocalKeyMetricAttestor(EthECKey key) => _key = key;

    public string Name => "local-key";

    public string Address => _key.GetPublicAddress();

    /// <summary>
    /// Signs a probe challenge (#225): the only other thing this key signs, so a gateway can give the network's metric
    /// signer a reserved lane. Domain-separated from metric reports, so it can never be reused as one.
    /// </summary>
    public string SignProbe(string gateway, long unixSeconds, byte[] nonce) =>
        new EthereumMessageSigner().Sign(ProbeChallenge.Digest(gateway, unixSeconds, nonce), _key);

    public Task<MetricAttestation> AttestAsync(MetricReport report, CancellationToken ct) =>
        Task.FromResult(new MetricAttestation(new EthereumMessageSigner().Sign(MetricReportDigest.Hash(report), _key), Address));

    public override string ToString() => $"LocalKeyMetricAttestor({Address})";

    /// <summary>Loads the key; throws <see cref="MetricSignerKeyException"/> when missing, malformed or group/world-readable.</summary>
    public static LocalKeyMetricAttestor Load(string path)
    {
        if (!File.Exists(path))
        {
            throw new MetricSignerKeyException($"Metric signer key file '{path}' is missing.");
        }

        if (!OperatingSystem.IsWindows() && (File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.OtherRead | UnixFileMode.OtherWrite)) != 0)
        {
            throw new MetricSignerKeyException($"Metric signer key file '{path}' is readable by other users; chmod 600 it.");
        }

        var hex = File.ReadAllText(path).Trim();
        try
        {
            return new LocalKeyMetricAttestor(new EthECKey(hex));
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException or IndexOutOfRangeException)
        {
            throw new MetricSignerKeyException($"Metric signer key file '{path}' isn't a secp256k1 private key.");
        }
    }

    /// <summary>Writes <paramref name="privateKey"/> owner-only from creation (LocalDev provisioning).</summary>
    public static void Write(string path, string privateKey)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        using var writer = new StreamWriter(path, System.Text.Encoding.UTF8, options);
        writer.Write(privateKey);
    }
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
