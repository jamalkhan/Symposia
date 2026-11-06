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

        if (string.IsNullOrEmpty(deployment.RewardDistributorAddress) || string.IsNullOrEmpty(deployment.StakingRegistryAddress)
            || !await HasCodeAsync(rpcUrl, deployment.RewardDistributorAddress, ct))
        {
            // Rewards stack (#70-#80) with its epoch controller (#72), so the hosted gateway's epoch sealer has something to settle.
            // Deployments from before #161 didn't record the staking registry, so they redeploy once.
            var (distributor, epochs, telemetry, staking) = await DeployRewardsAsync(network.FoundryBin, rpcUrl, ct);
            deployment = deployment with
            {
                RewardDistributorAddress = distributor, EpochControllerAddress = epochs, TelemetryRegistryAddress = telemetry, StakingRegistryAddress = staking,
            };
            SecureFile.Write(context.Paths.Deployment, deployment, ownerOnly: false);
            context.Logger.LogInformation("Deployed the rewards contracts (RewardDistributor {Distributor}, EpochController {Epochs}).", distributor, epochs);
        }

        return new NetworkInfo($"http://127.0.0.1:{network.GatewayPort}", AnvilChainId, deployment.NodeRegistryAddress, deployment.EpochRootRegistryAddress, rpcUrl,
            deployment.ProtocolActivationsAddress, deployment.RewardDistributorAddress, deployment.EpochControllerAddress,
            deployment.TelemetryRegistryAddress, deployment.StakingRegistryAddress);
    }

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
            preset.TelemetryRegistryAddress, NullIfEmpty(preset.StakingRegistryAddress));
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

        return new NetworkInfo(gateway.GetLeftPart(UriPartial.Authority), descriptor.ChainId, descriptor.NodeRegistryAddress, descriptor.EpochRootRegistryAddress, null,
            descriptor.ProtocolActivationsAddress);
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

    /// <summary>Runs DeployRewards.s.sol (dev roles = the anvil dev key) and returns the addresses the host uses.</summary>
    private static async Task<(string Distributor, string Epochs, string Telemetry, string Staking)> DeployRewardsAsync(
        string foundryBin, string rpcUrl, CancellationToken ct)
    {
        var output = await ForgeAsync(foundryBin, ct, "script", "script/DeployRewards.s.sol:DeployRewards", "--rpc-url", rpcUrl, "--private-key", AnvilDevKey, "--broadcast");
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

    private static async Task<string> ForgeAsync(string foundryBin, CancellationToken ct, params string[] args)
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

    private static Task<JsonElement?> TryChainIdAsync(string rpcUrl, CancellationToken ct) => RpcAsync(rpcUrl, "eth_chainId", [], ct);

    private static async Task<bool> HasCodeAsync(string rpcUrl, string address, CancellationToken ct)
    {
        var code = await RpcAsync(rpcUrl, "eth_getCode", [address, "latest"], ct);
        return code is { ValueKind: JsonValueKind.String } && code.Value.GetString() is { Length: > 2 };
    }

    /// <summary>Deployed addresses, tagged with the chain they were deployed to (checked on load).</summary>
    private sealed record Deployment(string NodeRegistryAddress, string EpochRootRegistryAddress, string RpcUrl, ulong ChainId,
        string? ProtocolActivationsAddress = null, string? RewardDistributorAddress = null, string? EpochControllerAddress = null,
        string? TelemetryRegistryAddress = null, string? StakingRegistryAddress = null);

    private sealed record GatewayNetwork(ulong ChainId, string NodeRegistryAddress, string EpochRootRegistryAddress, string? ProtocolActivationsAddress = null);
}
