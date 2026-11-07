using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Components;

/// <summary>
/// Resolves which chain this host's roles use (issue #133 FR-B):
/// <list type="bullet">
/// <item><c>LocalDev</c>: runs anvil as a child process with persisted state and deploys the contracts when missing.</item>
/// <item><c>RemoteRpc</c>: validates the chain preset; never deploys.</item>
/// <item><c>Join</c>: reads the network descriptor from the remote gateway's <c>/v1/network</c>.</item>
/// </list>
/// </summary>
public sealed partial class ChainComponent : HostComponent
{
    /// <summary>Anvil's well-known dev account #0. Only ever used against a local anvil chain.</summary>
    internal const string AnvilDevKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";

    private const ulong AnvilChainId = 31337;
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };

    private Process? _anvil;

    public override string Name => "chain";

    protected override string? SkipReason(HostContext context) =>
        context.Settings.Network.Mode == NetworkMode.Unconfigured ? "Network not configured yet" : null;

    protected override async Task OnStartAsync(HostContext context, CancellationToken ct)
    {
        var network = context.Settings.Network;
        context.Network = network.Mode switch
        {
            NetworkMode.LocalDev => await StartLocalDevAsync(context, ct),
            NetworkMode.RemoteRpc => ResolveRemoteRpc(context),
            NetworkMode.Join => await JoinAsync(network.GatewayUrl, ct),
            _ => throw new ComponentFaultException($"Unknown network mode {network.Mode}."),
        };
    }

    protected override async Task OnStopAsync()
    {
        var anvil = _anvil;
        _anvil = null;
        if (anvil is null || anvil.HasExited)
        {
            return;
        }

        // anvil only dumps --state on a clean exit, so ask nicely first.
        if (!OperatingSystem.IsWindows())
        {
            using var signal = Process.Start("kill", ["-INT", anvil.Id.ToString()]);
            signal?.WaitForExit();
        }

        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await anvil.WaitForExitAsync(wait.Token);
        }
        catch (OperationCanceledException)
        {
            anvil.Kill(entireProcessTree: true);
        }
    }

    private async Task<NetworkInfo> StartLocalDevAsync(HostContext context, CancellationToken ct)
    {
        var network = context.Settings.Network;
        var rpcUrl = $"http://127.0.0.1:{network.AnvilPort}";

        if (await TryChainIdAsync(rpcUrl, ct) is not null)
        {
            throw new ComponentFaultException(
                $"Port {network.AnvilPort} already has a chain on it (another host?). Use Join mode with that host's gateway URL, or change the anvil port.");
        }

        var anvilPath = FindFoundryTool(network.FoundryBin, "anvil");
        _anvil = Process.Start(new ProcessStartInfo
        {
            FileName = anvilPath,
            // --state-interval: also save periodically, so a crash or hard kill (or Windows, which
            // has no SIGINT) loses at most a few seconds of chain state.
            ArgumentList = { "--port", network.AnvilPort.ToString(), "--chain-id", AnvilChainId.ToString(), "--state", context.Paths.AnvilState, "--state-interval", "5", "--silent" },
            // Not redirected: undrained pipes would eventually block anvil.
            UseShellExecute = false,
        }) ?? throw new ComponentFaultException("Failed to start anvil.");

        await WaitForRpcAsync(rpcUrl, ct);

        var deployment = ReadDeployment(context);
        var reusable = deployment is not null && deployment.RpcUrl == rpcUrl && deployment.ChainId == AnvilChainId
            && await HasCodeAsync(rpcUrl, deployment.NodeRegistryAddress, ct);
        if (!reusable)
        {
            if (deployment is not null)
            {
                context.Logger.LogWarning(
                    "The local chain no longer has the previously deployed contracts (chain state was reset or the anvil port changed). " +
                    "Redeploying: all earlier on-chain registrations on this local network are gone, and joined hosts must restart to pick up the new addresses.");
            }

            deployment = await DeployAsync(network.FoundryBin, rpcUrl, ct);
            SecureFile.Write(context.Paths.Deployment, deployment, ownerOnly: false);
        }
        else if (string.IsNullOrEmpty(deployment!.ProtocolActivationsAddress) || !await HasCodeAsync(rpcUrl, deployment.ProtocolActivationsAddress, ct))
        {
            // Chains deployed before #145 lack ProtocolActivations: add it alone, keeping registrations.
            deployment = deployment with { ProtocolActivationsAddress = await DeployProtocolActivationsAsync(network.FoundryBin, rpcUrl, ct) };
            SecureFile.Write(context.Paths.Deployment, deployment, ownerOnly: false);
            context.Logger.LogInformation("Deployed ProtocolActivations at {Address} on the existing local chain.", deployment.ProtocolActivationsAddress);
        }

        var profile = new LocalDevRewardsProfile(LocalNodeAddress(context));
        var resetRequested = File.Exists(context.Paths.ResetRewardsFlag);
        context.RewardsStale = null;
        if (resetRequested || string.IsNullOrEmpty(deployment.RewardDistributorAddress) || deployment.RewardsVersion < RewardsStackVersion
            || !await HasCodeAsync(rpcUrl, deployment.RewardDistributorAddress, ct))
        {
            // Rewards stack (#70-#80) with its epoch controller (#72), so the hosted gateway's epoch sealer has something to settle.
            // A stack from an older build (lower RewardsVersion) lacks functions the gateway and host now call, so it redeploys.
            var deployBlock = await BlockNumberAsync(rpcUrl, ct);
            var (distributor, epochs, telemetry, staking) = await DeployRewardsAsync(network.FoundryBin, rpcUrl, profile, ct);
            deployment = deployment with
            {
                RewardDistributorAddress = distributor, EpochControllerAddress = epochs, TelemetryRegistryAddress = telemetry, StakingRegistryAddress = staking,
                RewardsVersion = RewardsStackVersion, RewardsDeployBlock = deployBlock,
            };
            SecureFile.Write(context.Paths.Deployment, deployment, ownerOnly: false);
            context.Logger.LogInformation("Deployed the rewards contracts (RewardDistributor {Distributor}, EpochController {Epochs}).", distributor, epochs);
            File.Delete(context.Paths.ResetRewardsFlag);
        }
        else
        {
            // A chain deployed with other timing or committee keeps working but can't settle with this host (#181 FR4):
            // say so instead of silently redeploying over the developer's ledger.
            context.RewardsStale = await CheckRewardsProfileAsync(rpcUrl, deployment, profile, ct);
            if (context.RewardsStale is { } stale)
            {
                context.Logger.LogWarning("Rewards can't settle on this local chain: {Reason} Reset the rewards contracts from Diagnostics (or run with --reset-local-rewards).",
                    stale.Reason);
            }
        }

        return ToNetworkInfo(deployment, network.GatewayPort, rpcUrl);
    }

    /// <summary>The one place a LocalDev deployment becomes the host's <see cref="NetworkInfo"/> (shared with a rewards reset).</summary>
    private static NetworkInfo ToNetworkInfo(Deployment deployment, int gatewayPort, string rpcUrl) =>
        new($"http://127.0.0.1:{gatewayPort}", AnvilChainId, deployment.NodeRegistryAddress, deployment.EpochRootRegistryAddress, rpcUrl,
            deployment.ProtocolActivationsAddress, deployment.RewardDistributorAddress, deployment.EpochControllerAddress,
            deployment.TelemetryRegistryAddress, deployment.StakingRegistryAddress, RpcSource.Direct, deployment.RewardsDeployBlock);

    private static NetworkInfo ResolveRemoteRpc(HostContext context)
    {
        var network = context.Settings.Network;
        var preset = ChainPresets.Resolve(network);
        if (!preset.IsDeployed)
        {
            throw new ComponentFaultException(
                $"Preset '{network.Preset}' is not yet deployed: it needs an RPC URL, chain ID and contract addresses before nodes can use it.");
        }

        if (!context.Secrets.HasRelayerKey)
        {
            throw new ComponentFaultException("RemoteRpc mode runs a local gateway and needs a relayer key. Set it in Settings.");
        }

        return new NetworkInfo($"http://127.0.0.1:{network.GatewayPort}", preset.ChainId, preset.NodeRegistryAddress, preset.EpochRootRegistryAddress, preset.RpcUrl,
            preset.ProtocolActivationsAddress, preset.RewardDistributorAddress, preset.EpochControllerAddress,
            preset.TelemetryRegistryAddress, NullIfEmpty(preset.StakingRegistryAddress), RpcSource.Direct, preset.DeployBlock);
    }

    internal static async Task<NetworkInfo> JoinAsync(string gatewayUrl, CancellationToken ct)
    {
        if (!Uri.TryCreate(gatewayUrl, UriKind.Absolute, out var gateway))
        {
            throw new ComponentFaultException($"Gateway URL '{gatewayUrl}' is not a valid URL.");
        }

        GatewayNetwork? descriptor;
        try
        {
            descriptor = await Http.GetFromJsonAsync<GatewayNetwork>(new Uri(gateway, "/v1/network"), SecureFile.Json, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException)
        {
            throw new ComponentFaultException($"Could not reach the gateway at {gatewayUrl} ({ex.Message}).");
        }

        if (descriptor is null || string.IsNullOrEmpty(descriptor.NodeRegistryAddress))
        {
            throw new ComponentFaultException($"Gateway at {gatewayUrl} returned no network descriptor.");
        }

        return FromDescriptor(gateway, descriptor);
    }

    /// <summary>
    /// Maps a gateway's descriptor to this host's network (issue #172). Chain reads go through the
    /// RPC the gateway advertises; an older gateway advertises none, so reads stay unavailable.
    /// </summary>
    internal static NetworkInfo FromDescriptor(Uri gateway, GatewayNetwork descriptor)
    {
        string? rpcUrl = null;
        var source = RpcSource.Direct;
        if (!string.IsNullOrWhiteSpace(descriptor.RpcUrl) && Uri.TryCreate(gateway, descriptor.RpcUrl, out var rpc)
            && rpc.Scheme is "http" or "https")
        {
            rpcUrl = rpc.ToString();
            source = rpc.AbsolutePath.TrimEnd('/') == "/v1/rpc" && rpc.Authority == gateway.Authority ? RpcSource.Gateway : RpcSource.Advertised;
        }

        return new NetworkInfo(gateway.GetLeftPart(UriPartial.Authority), descriptor.ChainId, descriptor.NodeRegistryAddress, descriptor.EpochRootRegistryAddress, rpcUrl,
            descriptor.ProtocolActivationsAddress, descriptor.RewardDistributorAddress, descriptor.EpochControllerAddress,
            descriptor.TelemetryRegistryAddress, descriptor.StakingRegistryAddress, source, descriptor.DeployBlock ?? 0);
    }

    private static readonly HttpClient ProbeHttp = new() { Timeout = TimeSpan.FromSeconds(1) };
    private static (DateTimeOffset At, string Url, bool Found) _lastProbe;

    /// <summary>Quickly probes a gateway URL (1s timeout, cached 15s); used by the UI to suggest joining a local network.</summary>
    internal static async Task<bool> GatewayRespondsAsync(string gatewayUrl, CancellationToken ct)
    {
        var last = _lastProbe;
        if (last.Url == gatewayUrl && DateTimeOffset.UtcNow - last.At < TimeSpan.FromSeconds(15))
        {
            return last.Found;
        }

        bool found;
        try
        {
            using var response = await ProbeHttp.GetAsync(new Uri(new Uri(gatewayUrl), "/v1/network"), ct);
            found = response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            found = false;
        }

        _lastProbe = (DateTimeOffset.UtcNow, gatewayUrl, found);
        return found;
    }

    private static Deployment? ReadDeployment(HostContext context)
    {
        try
        {
            return SecureFile.Read<Deployment>(context.Paths.Deployment);
        }
        catch (CorruptFileException)
        {
            return null; // Recoverable: just redeploy.
        }
    }

    private static async Task<Deployment> DeployAsync(string foundryBin, string rpcUrl, CancellationToken ct)
    {
        var output = await ForgeAsync(foundryBin, ct, "script", "script/Deploy.s.sol:Deploy", "--rpc-url", rpcUrl, "--private-key", AnvilDevKey, "--broadcast");
        return new Deployment(Extract(output, "NodeRegistry"), Extract(output, "EpochRootRegistry"), rpcUrl, AnvilChainId, Extract(output, "ProtocolActivations"));
    }

    /// <summary>
    /// Runs DeployRewards.s.sol (dev roles = the anvil dev key) with the LocalDev timing and this
    /// node as the telemetry committee (#181), and returns the addresses the host uses.
    /// </summary>
    private static async Task<(string Distributor, string Epochs, string Telemetry, string Staking)> DeployRewardsAsync(
        string foundryBin, string rpcUrl, LocalDevRewardsProfile profile, CancellationToken ct)
    {
        var output = await ForgeAsync(foundryBin, profile.ToEnvironment(), ct,
            "script", "script/DeployRewards.s.sol:DeployRewards", "--rpc-url", rpcUrl, "--private-key", AnvilDevKey, "--broadcast");
        return (Extract(output, "RewardDistributor"), Extract(output, "EpochController"), Extract(output, "TelemetryCheckpointRegistry"),
            Extract(output, "StakingNodeRegistry"));
    }

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    /// <summary>Deploys ProtocolActivations (issue #145) owned by the dev key, with no notice window (local dev).</summary>
    private static async Task<string> DeployProtocolActivationsAsync(string foundryBin, string rpcUrl, CancellationToken ct)
    {
        var owner = new Nethereum.Signer.EthECKey(AnvilDevKey).GetPublicAddress();
        var output = await ForgeAsync(foundryBin, ct, "create", "src/ProtocolActivations.sol:ProtocolActivations", "--rpc-url", rpcUrl,
            "--private-key", AnvilDevKey, "--broadcast", "--constructor-args", owner, "0");
        return Extract(output, "Deployed to");
    }

    private static Task<string> ForgeAsync(string foundryBin, CancellationToken ct, params string[] args) => ForgeAsync(foundryBin, null, ct, args);

    private static async Task<string> ForgeAsync(string foundryBin, IReadOnlyDictionary<string, string>? env, CancellationToken ct, params string[] args)
    {
        var contractsDir = FindContractsDir()
            ?? throw new ComponentFaultException(
                "LocalDev deploys contracts from a Symposia repo checkout (Blockchain/bootstrap-chain) and none was found. Run from the repo or set SYMPOSIA_REPO_ROOT.");

        var start = new ProcessStartInfo
        {
            FileName = FindFoundryTool(foundryBin, "forge"),
            WorkingDirectory = contractsDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args)
        {
            start.ArgumentList.Add(arg);
        }

        foreach (var (name, value) in env ?? new Dictionary<string, string>())
        {
            start.Environment[name] = value;
        }

        var forge = Process.Start(start) ?? throw new ComponentFaultException("Failed to start forge.");
        var stdout = forge.StandardOutput.ReadToEndAsync(ct);
        var stderr = forge.StandardError.ReadToEndAsync(ct);
        try
        {
            await forge.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            // Don't leave a half-done deploy running against the chain.
            forge.Kill(entireProcessTree: true);
            throw;
        }
        if (forge.ExitCode != 0)
        {
            throw new ComponentFaultException($"Contract deployment failed: {(await stderr).Trim()} {(await stdout).Trim()}".Trim());
        }

        return await stdout;
    }

    private static string Extract(string output, string label)
    {
        var match = Regex.Match(output, $@"{label}:\s*(0x[a-fA-F0-9]{{40}})");
        return match.Success ? match.Groups[1].Value : throw new ComponentFaultException($"Deployment output did not include the {label} address.");
    }

    internal static string? FindContractsDir()
    {
        foreach (var start in new[] { Environment.GetEnvironmentVariable("SYMPOSIA_REPO_ROOT"), AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            for (var dir = string.IsNullOrEmpty(start) ? null : new DirectoryInfo(start); dir is not null; dir = dir.Parent)
            {
                var candidate = Path.Combine(dir.FullName, "Blockchain", "bootstrap-chain");
                if (File.Exists(Path.Combine(candidate, "foundry.toml")))
                {
                    return candidate;
                }
            }
        }

        return null;
    }

    private static string FindFoundryTool(string foundryBin, string tool)
    {
        var dirs = new List<string>();
        if (!string.IsNullOrWhiteSpace(foundryBin))
        {
            dirs.Add(foundryBin);
        }

        dirs.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "bin"));
        dirs.AddRange((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries));

        var exe = OperatingSystem.IsWindows() ? tool + ".exe" : tool;
        return dirs.Select(d => Path.Combine(d, exe)).FirstOrDefault(File.Exists)
            ?? throw new ComponentFaultException(
                $"'{tool}' not found. Install Foundry (curl -L https://foundry.paradigm.xyz | bash && foundryup) or set the Foundry bin directory in Settings.");
    }

    private static async Task WaitForRpcAsync(string rpcUrl, CancellationToken ct)
    {
        for (var i = 0; i < 50; i++)
        {
            if (await TryChainIdAsync(rpcUrl, ct) is not null)
            {
                return;
            }

            await Task.Delay(200, ct);
        }

        throw new ComponentFaultException($"anvil did not answer on {rpcUrl}.");
    }

    internal static async Task<JsonElement?> RpcAsync(string rpcUrl, string method, object[] parameters, CancellationToken ct)
    {
        try
        {
            using var response = await Http.PostAsJsonAsync(rpcUrl, new { jsonrpc = "2.0", id = 1, method, @params = parameters }, ct);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var body = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
            return body.TryGetProperty("result", out var result) ? result : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException && !ct.IsCancellationRequested)
        {
            return null;
        }
    }

    /// <summary>This host's node address, the LocalDev telemetry committee (#181 FR2).</summary>
    private static string LocalNodeAddress(HostContext context)
    {
        if (!File.Exists(context.Paths.NodeKey))
        {
            throw new ComponentFaultException("LocalDev needs this host's node key to make it the telemetry committee, and none was found.");
        }

        return new Symposia.Network.Node.NodeIdentity(Microsoft.Extensions.Options.Options.Create(
            new Symposia.Network.Node.NodeNetworkOptions { NodeIdentityKeyPath = context.Paths.NodeKey })).NodeId;
    }

    /// <summary>Compares the deployed epoch length and committee with the LocalDev profile; null when they match.</summary>
    internal static async Task<RewardsStaleNotice?> CheckRewardsProfileAsync(string rpcUrl, Deployment deployment, LocalDevRewardsProfile profile, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(deployment.EpochControllerAddress) || string.IsNullOrEmpty(deployment.TelemetryRegistryAddress))
        {
            return null;
        }

        var web3 = new Nethereum.Web3.Web3(rpcUrl);
        var epochSeconds = (ulong)await web3.Eth.GetContractQueryHandler<EpochLengthFunction>()
            .QueryAsync<System.Numerics.BigInteger>(deployment.EpochControllerAddress, new EpochLengthFunction());
        var committee = await web3.Eth.GetContractQueryHandler<CommitteeFunction>()
            .QueryDeserializingToObjectAsync<CommitteeOutput>(new CommitteeFunction(), deployment.TelemetryRegistryAddress);
        var members = committee.Members ?? [];

        var reasons = new List<string>();
        if (epochSeconds != profile.EpochLengthSeconds)
        {
            reasons.Add($"it was deployed with {epochSeconds}s epochs (LocalDev uses {profile.EpochLengthSeconds}s)");
        }

        if (!members.Any(m => string.Equals(m, profile.CommitteeAddress, StringComparison.OrdinalIgnoreCase)))
        {
            reasons.Add($"its telemetry committee is {string.Join(", ", members)}, not this node ({profile.CommitteeAddress})");
        }

        return reasons.Count == 0
            ? null
            : new RewardsStaleNotice(string.Join("; ", reasons) + ".", epochSeconds, members, profile.EpochLengthSeconds, profile.CommitteeAddress);
    }

    [Nethereum.ABI.FunctionEncoding.Attributes.Function("epochLength", "uint256")]
    private sealed class EpochLengthFunction : Nethereum.Contracts.FunctionMessage;

    [Nethereum.ABI.FunctionEncoding.Attributes.Function("committee")]
    private sealed class CommitteeFunction : Nethereum.Contracts.FunctionMessage;

    [Nethereum.ABI.FunctionEncoding.Attributes.FunctionOutput]
    private sealed class CommitteeOutput : Nethereum.ABI.FunctionEncoding.Attributes.IFunctionOutputDTO
    {
        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("address[]", "members", 1)]
        public List<string>? Members { get; set; }

        [Nethereum.ABI.FunctionEncoding.Attributes.Parameter("uint256", "threshold", 2)]
        public System.Numerics.BigInteger Threshold { get; set; }
    }

    private static async Task<ulong> BlockNumberAsync(string rpcUrl, CancellationToken ct) =>
        await RpcAsync(rpcUrl, "eth_blockNumber", [], ct) is { ValueKind: JsonValueKind.String } hex
            ? Convert.ToUInt64(hex.GetString(), 16)
            : 0;

    private static Task<JsonElement?> TryChainIdAsync(string rpcUrl, CancellationToken ct) => RpcAsync(rpcUrl, "eth_chainId", [], ct);

    private static async Task<bool> HasCodeAsync(string rpcUrl, string address, CancellationToken ct)
    {
        var code = await RpcAsync(rpcUrl, "eth_getCode", [address, "latest"], ct);
        return code is { ValueKind: JsonValueKind.String } && code.Value.GetString() is { Length: > 2 };
    }

    /// <summary>Deployed addresses, tagged with the chain they were deployed to (checked on load).</summary>
    internal sealed record Deployment(string NodeRegistryAddress, string EpochRootRegistryAddress, string RpcUrl, ulong ChainId,
        string? ProtocolActivationsAddress = null, string? RewardDistributorAddress = null, string? EpochControllerAddress = null,
        string? TelemetryRegistryAddress = null, string? StakingRegistryAddress = null, int RewardsVersion = 0, ulong RewardsDeployBlock = 0);

    /// <summary>
    /// Bump whenever the rewards contracts gain a function the gateway or host calls, so existing local
    /// chains redeploy the rewards stack instead of failing against old bytecode. 1 = #161 (staking
    /// registry address), 2 = #165/#166 (firstReportBlock, per-service stages, external libraries).
    /// </summary>
    internal const int RewardsStackVersion = 2;

    /// <summary>A gateway's <c>/v1/network</c> descriptor; fields after the registries are absent on older gateways.</summary>
    internal sealed record GatewayNetwork(ulong ChainId, string NodeRegistryAddress, string EpochRootRegistryAddress, string? ProtocolActivationsAddress = null,
        string? RewardDistributorAddress = null, string? EpochControllerAddress = null, string? TelemetryRegistryAddress = null,
        string? StakingRegistryAddress = null, string? RpcUrl = null, ulong? DeployBlock = null);
}
