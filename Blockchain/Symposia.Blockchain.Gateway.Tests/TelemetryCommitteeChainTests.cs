using System.Numerics;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Nethereum.Signer;
using Nethereum.Web3;
using Symposia.Network.Node.Telemetry;
using Symposia.Network.Node.Telemetry.Committee;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// Issue #158 end to end on a real chain and the real Gateway relay: a one-member committee
/// checkpoints window 0, the node self-checkpoints window 1 after the committee stays silent, and
/// after the telemetry deadline the committee's records land and finalize. This proves the C#
/// EIP-712 digests, batch encoding and records hash match the contract byte for byte.
/// </summary>
public sealed class TelemetryCommitteeChainTests(RewardsChainFixture chain) : IClassFixture<RewardsChainFixture>, IDisposable
{
    private readonly string _blobs = Path.Combine(Path.GetTempPath(), "sym158-e2e-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_blobs))
        {
            Directory.Delete(_blobs, recursive: true);
        }
    }

    private const string Abi = """
    [
      {"type":"function","name":"mint","inputs":[{"name":"to","type":"address"},{"name":"amount","type":"uint256"}],"outputs":[],"stateMutability":"nonpayable"},
      {"type":"function","name":"approve","inputs":[{"name":"s","type":"address"},{"name":"a","type":"uint256"}],"outputs":[{"type":"bool"}],"stateMutability":"nonpayable"},
      {"type":"function","name":"minStakeFor","inputs":[{"name":"t","type":"uint8"},{"name":"c","type":"uint256"}],"outputs":[{"type":"uint256"}],"stateMutability":"view"},
      {"type":"function","name":"register","inputs":[{"name":"t","type":"uint8"},{"name":"c","type":"uint256"},{"name":"r","type":"bytes32"},{"name":"s","type":"uint256"}],"outputs":[],"stateMutability":"nonpayable"},
      {"type":"function","name":"recordVerification","inputs":[{"name":"n","type":"address"},{"name":"p","type":"bool"},{"name":"e","type":"uint256"}],"outputs":[],"stateMutability":"nonpayable"},
      {"type":"function","name":"recordsFinalized","inputs":[{"name":"e","type":"uint256"}],"outputs":[{"type":"bool"}],"stateMutability":"view"},
      {"type":"function","name":"getEpochMetricRecord","inputs":[{"name":"n","type":"address"},{"name":"e","type":"uint256"}],"outputs":[{"type":"tuple","components":[
        {"name":"selfReported","type":"tuple","components":[{"name":"a","type":"uint32"},{"name":"b","type":"uint64"},{"name":"c","type":"uint64"},{"name":"d","type":"uint64"},{"name":"e","type":"uint64"},{"name":"f","type":"uint32"},{"name":"g","type":"uint32"}]},
        {"name":"verifierAttested","type":"tuple","components":[{"name":"a","type":"uint32"},{"name":"b","type":"uint64"},{"name":"c","type":"uint64"},{"name":"d","type":"uint64"},{"name":"e","type":"uint64"},{"name":"f","type":"uint32"},{"name":"g","type":"uint32"}]},
        {"name":"heartbeatsReceived","type":"uint32"},{"name":"heartbeatsExpected","type":"uint32"},{"name":"heartbeatComplianceBps","type":"uint16"},
        {"name":"attestationCount","type":"uint16"},{"name":"firstCheckpoint","type":"uint16"},{"name":"lastCheckpoint","type":"uint16"},
        {"name":"eligible","type":"bool"},{"name":"divergenceFlag","type":"bool"},{"name":"exists","type":"bool"}]}],"stateMutability":"view"}
    ]
    """;

    [Fact]
    public async Task CommitteeCheckpointsSelfCheckpointFallbackAndRecordsLandOnChain()
    {
        var a = chain.Addresses;
        var registry = a["TelemetryCheckpointRegistry"];
        var networkBinding = a["StakingNodeRegistry"]; // what samples are signed against
        var deployer = new Web3(new Nethereum.Web3.Accounts.Account(RewardsChainFixture.DeployerKey), chain.RpcUrl);
        var nodeKey = new EthECKey(RewardsChainFixture.NodeKey);
        var nodeWeb3 = new Web3(new Nethereum.Web3.Accounts.Account(RewardsChainFixture.NodeKey), chain.RpcUrl);
        var node = nodeKey.GetPublicAddress();
        deployer.TransactionManager.UseLegacyAsDefault = nodeWeb3.TransactionManager.UseLegacyAsDefault = true;
        const ulong chainId = 31337;

        // The node must be Active to self-checkpoint.
        var stake = await deployer.Eth.GetContract(Abi, a["StakingNodeRegistry"]).GetFunction("minStakeFor").CallAsync<BigInteger>((byte)0, BigInteger.One);
        await Send(deployer, a["SYMToken"], "mint", node, stake);
        await Send(nodeWeb3, a["SYMToken"], "approve", a["StakingNodeRegistry"], stake);
        await Send(nodeWeb3, a["StakingNodeRegistry"], "register", (byte)0, BigInteger.One, new byte[32], stake);
        await Send(deployer, a["StakingNodeRegistry"], "recordVerification", node, true, BigInteger.Zero); // Pending -> Active

        await using var gateway = new WebApplicationFactory<Program>().WithWebHostBuilder(b => b.ConfigureAppConfiguration((_, c) =>
            c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:RpcUrl"] = chain.RpcUrl,
                ["Gateway:RelayerPrivateKey"] = RewardsChainFixture.DeployerKey,
                ["Gateway:TelemetryRegistryAddress"] = registry,
                ["Gateway:TelemetryBlobs:CacheDirectory"] = _blobs,
            })));
        var relay = new HttpTelemetryRelay(gateway.CreateClient());
        var telemetryChain = new NethereumTelemetryChain(new TelemetryChainOptions
        {
            RpcUrl = chain.RpcUrl,
            TelemetryRegistryAddress = registry,
            EpochControllerAddress = a["EpochController"],
        });
        var schedule = await telemetryChain.ScheduleAsync(default);
        var eip712 = new TelemetryEip712(chainId, registry);
        var blobs = new CachingTelemetryBlobStore(_blobs, null); // same store the Gateway writes relayed batches to

        // One-member committee: the deployer (DeployRewards' default committee, threshold 1).
        var member = new KeySigner(RewardsChainFixture.DeployerKey);
        var store = new InMemoryTelemetryGossip(chainId, networkBinding);
        var gossip = new TelemetryGossipNode(store, new NoPeers(), member, eip712, TimeProvider.System,
            Options.Create(new TelemetryGossipOptions()), chainId);
        var committee = new CommitteeWorker(telemetryChain, relay, blobs, gossip, member, eip712, new NoHttp(),
            Options.Create(new TelemetryCommitteeOptions { SettleDelaySeconds = 1, ProposalTimeoutSeconds = 600 }),
            chainId, networkBinding, NullLogger<CommitteeWorker>.Instance);
        var nodeStore = new InMemoryTelemetryGossip(chainId, networkBinding);
        var fallback = new SelfCheckpointWorker(telemetryChain, relay, nodeStore, new KeySigner(RewardsChainFixture.NodeKey), eip712, chainId,
            networkBinding, NullLogger<SelfCheckpointWorker>.Instance);

        // Window 0 reaches the committee; window 1 stays with the node.
        for (uint slot = 0; slot < 24; slot++)
        {
            var beat = TelemetrySigning.Sign(nodeKey, new HeartbeatSample(node, 0, slot, schedule.EpochStart(0) + slot * 300 + 5,
                1_000, 800, 50_000, 40_000, 50, 120, 5_000, 1_000), chainId, networkBinding);
            await nodeStore.PublishAsync(beat);
            if (slot < 12)
            {
                await store.PublishAsync(beat);
            }
        }

        await Warp(schedule.CheckpointWindowEnd(0, 0) + 2);
        await committee.RunOnceAsync(default);
        Assert.Equal(1, committee.Metrics.Submissions.Value);
        Assert.NotNull(await telemetryChain.CheckpointAsync(0, 0, default));

        await Warp(schedule.CheckpointWindowEnd(0, 1) + await telemetryChain.SelfCheckpointGraceAsync(default));
        await committee.RunOnceAsync(default); // nothing to propose for window 1: the committee never saw it
        await fallback.RunOnceAsync(default);
        Assert.Equal(1, fallback.SelfCheckpoints);
        Assert.NotNull(await telemetryChain.SelfCheckpointAsync(0, 1, node, default));

        await Warp(await telemetryChain.TelemetryDeadlineAsync(0, default) + 1);
        await committee.RunOnceAsync(default);

        var contract = deployer.Eth.GetContract(Abi, registry);
        Assert.True(await contract.GetFunction("recordsFinalized").CallAsync<bool>(BigInteger.Zero));
        var record = await contract.GetFunction("getEpochMetricRecord").CallDecodingToDefaultAsync(node, BigInteger.Zero);
        var fields = (List<Nethereum.ABI.FunctionEncoding.ParameterOutput>)record[0].Result;
        Assert.Equal(new BigInteger(24), (BigInteger)fields.Single(f => f.Parameter.Name == "heartbeatsReceived").Result);
        Assert.True((bool)fields.Single(f => f.Parameter.Name == "eligible").Result);
    }

    private async Task Warp(long timestamp)
    {
        using var http = new HttpClient();
        foreach (var body in new[]
                 {
                     $$"""{"jsonrpc":"2.0","method":"evm_setNextBlockTimestamp","params":[{{timestamp}}],"id":1}""",
                     """{"jsonrpc":"2.0","method":"evm_mine","params":[],"id":2}""",
                 })
        {
            (await http.PostAsync(chain.RpcUrl, new StringContent(body, System.Text.Encoding.UTF8, "application/json"))).EnsureSuccessStatusCode();
        }
    }

    private static async Task Send(Web3 web3, string address, string function, params object[] args)
    {
        var receipt = await web3.Eth.GetContract(Abi, address).GetFunction(function)
            .SendTransactionAndWaitForReceiptAsync(web3.TransactionManager.Account.Address, new Nethereum.Hex.HexTypes.HexBigInteger(3_000_000), null, null, args);
        Assert.Equal(BigInteger.One, receipt.Status.Value);
    }

    private sealed class KeySigner(string privateKey) : ITelemetrySigner
    {
        private readonly EthECKey _key = new(privateKey);

        public string Address => _key.GetPublicAddress();

        public byte[] Sign(byte[] digest)
        {
            var s = _key.SignAndCalculateV(digest);
            static byte[] Pad(byte[] b) => b.Length >= 32 ? b[^32..] : [.. new byte[32 - b.Length], .. b];
            return [.. Pad(s.R), .. Pad(s.S), s.V[0]];
        }
    }

    private sealed class NoPeers : ITelemetryPeers
    {
        public IReadOnlyList<(string Address, string Endpoint)> Peers() => [];
    }

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new();
    }
}
