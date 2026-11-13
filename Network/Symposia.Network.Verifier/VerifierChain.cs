using System.Net;
using System.Net.Http.Json;
using System.Numerics;
using Nethereum.Util;
using Nethereum.Web3;
using Symposia.Blockchain.Contracts;
using Symposia.Network.Node;
using Wire = Symposia.Network.Node.Rewards.ServiceMetricsRelayWire;

namespace Symposia.Network.Verifier;

/// <summary>Where the Verifier role reads the chain and sends reports (#225). Built by the Node Host per network.</summary>
public sealed class VerifierOptions
{
    public ulong ChainId { get; set; }

    public string RpcUrl { get; set; } = "";

    /// <summary>The Blockchain Gateway: node listing, the relay and the monitor-health reference.</summary>
    public string BlockchainGatewayUrl { get; set; } = "";

    public string NodeRegistryAddress { get; set; } = "";

    public string StakingRegistryAddress { get; set; } = "";

    public string RewardDistributorAddress { get; set; } = "";

    public string EpochControllerAddress { get; set; } = "";

    /// <summary>This host's node address: never measured off LocalDev, and never allowed as the signer.</summary>
    public string NodeAddress { get; set; } = "";

    public string SignerKeyPath { get; set; } = "";

    public string DataDir { get; set; } = "";

    /// <summary>Any one answering keeps a minute healthy; skipped on LocalDev.</summary>
    public IReadOnlyList<string> ExternalReferences { get; set; } =
        ["https://www.google.com/generate_204", "https://cloudflare.com/cdn-cgi/trace", "https://1.1.1.1/cdn-cgi/trace"];

    /// <summary>A bytes-served figure is capped at 10x the median, never below this per epoch.</summary>
    public long BytesServedFloor { get; set; } = 1L << 30;

    public bool IsLocalDev => ChainId == Node.Staking.DevFunding.LocalDevChainId;

    /// <summary>Whether a discovered URL may be probed off LocalDev; tests replace the DNS-backed default.</summary>
    internal Func<Uri, CancellationToken, Task<bool>> IsPublicTarget { get; set; } = TargetDirectory.IsPublicAsync;
}

/// <summary>The chain reads the Verifier needs; read-only.</summary>
public interface IVerifierChain
{
    Task<ulong> ChainIdAsync(CancellationToken ct);

    Task<ulong> CurrentEpochAsync(CancellationToken ct);

    Task<long> EpochStartAsync(ulong epoch, CancellationToken ct);

    Task<bool> AcceptsReportsAsync(ulong epoch, CancellationToken ct);

    Task<string> MetricSignerAsync(CancellationToken ct);

    /// <summary>Every registered node with <paramref name="service"/> Active, and its on-chain primary endpoint.</summary>
    Task<IReadOnlyList<RegisteredNode>> ActiveAsync(byte service, CancellationToken ct);

    /// <summary>VerifierAssignments (#227); "" when the network has none.</summary>
    Task<string> AssignmentsContractAsync(CancellationToken ct) => Task.FromResult("");

    Task<bool> SeededAsync(ulong epoch, CancellationToken ct) => Task.FromResult(false);

    /// <summary>This Verifier's (target, slot) assignments for <paramref name="epoch"/>, all pages.</summary>
    Task<IReadOnlyList<(string Target, byte Slot)>> AssignmentsOfAsync(ulong epoch, string verifier, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<(string, byte)>>([]);

    /// <summary>Assigned / submitted / judged / upheld for the card.</summary>
    Task<(uint Assigned, uint Submitted, uint Judged, uint Upheld)?> CountsAsync(ulong epoch, string verifier, CancellationToken ct) =>
        Task.FromResult<(uint, uint, uint, uint)?>(null);
}

public sealed class NethereumVerifierChain(VerifierOptions options, INodeRegistryClient registry) : IVerifierChain
{
    /// <summary><c>INodeRegistry.NodeStatus.Active</c>.</summary>
    private const byte Active = 2;

    private static readonly byte[] MetricSignerKey = Sha3Keccack.Current.CalculateHash(System.Text.Encoding.UTF8.GetBytes("reward.metric.signer"));

    private readonly Web3 _web3 = new(options.RpcUrl);

    public async Task<ulong> ChainIdAsync(CancellationToken ct) => (ulong)(await _web3.Eth.ChainId.SendRequestAsync()).Value;

    public async Task<ulong> CurrentEpochAsync(CancellationToken ct) => (ulong)await _web3.Eth.GetContractQueryHandler<CurrentEpochFunction>()
        .QueryAsync<BigInteger>(options.EpochControllerAddress, new CurrentEpochFunction());

    public async Task<long> EpochStartAsync(ulong epoch, CancellationToken ct) => (long)await _web3.Eth.GetContractQueryHandler<EpochStartFunction>()
        .QueryAsync<BigInteger>(options.EpochControllerAddress, new EpochStartFunction { Epoch = epoch });

    public Task<bool> AcceptsReportsAsync(ulong epoch, CancellationToken ct) => _web3.Eth.GetContractQueryHandler<AcceptsReportsFunction>()
        .QueryAsync<bool>(options.EpochControllerAddress, new AcceptsReportsFunction { Epoch = epoch });

    public async Task<string> MetricSignerAsync(CancellationToken ct)
    {
        var config = await _web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(options.RewardDistributorAddress, new ConfigFunction());
        return await _web3.Eth.GetContractQueryHandler<GetAddressFunction>().QueryAsync<string>(config, new GetAddressFunction { Key = MetricSignerKey });
    }

    private string? _assignments;

    public async Task<string> AssignmentsContractAsync(CancellationToken ct)
    {
        if (_assignments is { } known)
        {
            return known;
        }

        var config = await _web3.Eth.GetContractQueryHandler<ConfigFunction>().QueryAsync<string>(options.RewardDistributorAddress, new ConfigFunction());
        var address = await _web3.Eth.GetContractQueryHandler<GetAddressFunction>().QueryAsync<string>(config, new GetAddressFunction { Key = ConfigKeys.VerifierAssignments });
        return _assignments = string.IsNullOrEmpty(address) || address == "0x0000000000000000000000000000000000000000" ? "" : address;
    }

    public async Task<bool> SeededAsync(ulong epoch, CancellationToken ct) => await AssignmentsContractAsync(ct) is { Length: > 0 } a
        && (await _web3.Eth.GetContractQueryHandler<SeedOfFunction>().QueryAsync<byte[]>(a, new SeedOfFunction { Epoch = epoch })).Any(b => b != 0);

    public async Task<IReadOnlyList<(string Target, byte Slot)>> AssignmentsOfAsync(ulong epoch, string verifier, CancellationToken ct)
    {
        var contract = await AssignmentsContractAsync(ct);
        var found = new List<(string, byte)>();
        if (contract.Length == 0)
        {
            return found;
        }

        var total = (ulong)await _web3.Eth.GetContractQueryHandler<VerifierTargetCountFunction>()
            .QueryAsync<BigInteger>(contract, new VerifierTargetCountFunction { Epoch = epoch });
        const ulong page = 250; // fits the default eth_call gas cap (#227 arch §4)
        for (ulong offset = 0; offset < total; offset += page)
        {
            var result = await _web3.Eth.GetContractQueryHandler<AssignmentsOfFunction>().QueryDeserializingToObjectAsync<AssignmentsPageOutput>(
                new AssignmentsOfFunction { Epoch = epoch, Verifier = verifier, Offset = offset, Limit = page }, contract);
            found.AddRange(result.Targets.Zip(result.Slots, (t, s) => (t, s)));
        }

        return found;
    }

    public async Task<(uint Assigned, uint Submitted, uint Judged, uint Upheld)?> CountsAsync(ulong epoch, string verifier, CancellationToken ct)
    {
        var contract = await AssignmentsContractAsync(ct);
        if (contract.Length == 0)
        {
            return null;
        }

        var c = await _web3.Eth.GetContractQueryHandler<VerifierCountsOfFunction>().QueryDeserializingToObjectAsync<VerifierCountsOutput>(
            new VerifierCountsOfFunction { Epoch = epoch, Verifier = verifier }, contract);
        return (c.Assigned, c.Submitted, c.Judged, c.Upheld);
    }

    public async Task<IReadOnlyList<RegisteredNode>> ActiveAsync(byte service, CancellationToken ct)
    {
        var nodes = await registry.ListNodesAsync(ct);
        var active = new List<RegisteredNode>();
        foreach (var node in nodes)
        {
            var status = await _web3.Eth.GetContractQueryHandler<ServiceStatusOfFunction>()
                .QueryAsync<byte>(options.StakingRegistryAddress, new ServiceStatusOfFunction { Node = node.Node, Service = service });
            if (status == Active)
            {
                active.Add(node);
            }
        }

        return active;
    }
}

/// <summary>What the relay said: <see cref="Terminal"/> means stop trying this report.</summary>
public sealed record RelayOutcome(bool Submitted, bool Terminal, string? TransactionHash, string? Error);

public interface IReportRelay
{
    Task<RelayOutcome> SubmitAsync(Wire.Request request, CancellationToken ct);

    /// <summary>A Verifier attestation batch (#227).</summary>
    Task<RelayOutcome> AttestAsync(Symposia.Network.Node.Rewards.VerifierAttestationWire.Request request, CancellationToken ct) =>
        Task.FromResult(new RelayOutcome(false, true, null, "not supported"));

    /// <summary>Syncs this node into the Verifier pools (#227); a Verifier-only host isn't in the node registry.</summary>
    Task<RelayOutcome> SyncAsync(string node, CancellationToken ct) => Task.FromResult(new RelayOutcome(true, false, null, null));
}

/// <summary>Posts reports to the Blockchain Gateway's relay (#221), which pays the gas.</summary>
public sealed class HttpReportRelay(HttpClient http) : IReportRelay
{
    public Task<RelayOutcome> SubmitAsync(Wire.Request request, CancellationToken ct) => PostAsync(Wire.Path, request, ct);

    public Task<RelayOutcome> AttestAsync(Symposia.Network.Node.Rewards.VerifierAttestationWire.Request request, CancellationToken ct) =>
        PostAsync(Symposia.Network.Node.Rewards.VerifierAttestationWire.Path, request, ct);

    public Task<RelayOutcome> SyncAsync(string node, CancellationToken ct) =>
        PostAsync(Symposia.Network.Node.Rewards.VerifierAttestationWire.SyncPath, new Symposia.Network.Node.Rewards.VerifierAttestationWire.SyncRequest(node), ct);

    private async Task<RelayOutcome> PostAsync<T>(string path, T request, CancellationToken ct)
    {
        try
        {
            using var response = await http.PostAsJsonAsync(path, request, ct);
            Wire.Response? body = null;
            try
            {
                body = await response.Content.ReadFromJsonAsync<Wire.Response>(ct);
            }
            catch (Exception ex) when (ex is System.Text.Json.JsonException or NotSupportedException)
            {
            }

            return response.StatusCode switch
            {
                HttpStatusCode.OK => new RelayOutcome(true, false, body?.TransactionHash, null),
                // 409 is a terminal revert and 400 a malformed report; anything else is retried until the window closes.
                HttpStatusCode.Conflict or HttpStatusCode.BadRequest =>
                    new RelayOutcome(false, true, body?.TransactionHash, body?.Error ?? $"HTTP {(int)response.StatusCode}"),
                _ => new RelayOutcome(false, false, body?.TransactionHash, body?.Error ?? $"HTTP {(int)response.StatusCode}"),
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            return new RelayOutcome(false, false, null, ex.Message);
        }
    }
}
