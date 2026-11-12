using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Symposia.Blockchain.Gateway.Chain;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// Boots a real local anvil chain, deploys the NodeRegistry/EpochRootRegistry
/// contracts onto it via `forge script`, and hosts the Gateway (in-process,
/// via WebApplicationFactory) configured against that live deployment.
///
/// This mirrors the QA plan's requirement (issue #110) that registration,
/// rejection, and idempotency guarantees be exercised against real chain
/// state and real signature verification, not an in-memory mock.
/// </summary>
public sealed class ChainFixture : WebApplicationFactory<Program>, IAsyncLifetime
{
    // Anvil's well-known default account #0 — used here purely as the
    // deployer/relayer key in a disposable local devnet, never a real key.
    private const string DeployerPrivateKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    public const ulong ChainId = 31337;

    private AnvilProcess? _anvil;

    public string RpcUrl => _anvil?.RpcUrl ?? throw new InvalidOperationException("anvil has not started.");
    public string NodeRegistryAddress { get; private set; } = "";
    public string EpochRootRegistryAddress { get; private set; } = "";
    public string ProtocolActivationsAddress { get; private set; } = "";
    public string RelayerPrivateKey => DeployerPrivateKey;

    private static string BootstrapChainDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Blockchain", "bootstrap-chain")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        if (dir is null)
        {
            throw new InvalidOperationException("Could not locate Blockchain/bootstrap-chain from test base directory.");
        }

        return Path.Combine(dir, "Blockchain", "bootstrap-chain");
    }

    public async Task InitializeAsync()
    {
        try
        {
            await StartChainAsync();
        }
        catch
        {
            // A failed deploy must not orphan anvil: xUnit doesn't dispose a fixture whose initialization threw.
            await StopAnvilAsync();
            throw;
        }
    }

    private async Task StartChainAsync()
    {
        var foundryBin = AnvilProcess.FoundryBin;

        _anvil = await AnvilProcess.StartAsync();

        // Same process-wide gate as RewardsChainFixture: concurrent forge runs over one project can stall (#176).
        await RewardsChainFixture.ForgeGate.WaitAsync();
        string stdout, stderr;
        int exitCode;
        try
        {
            using var deploy = Process.Start(new ProcessStartInfo
            {
                FileName = Path.Combine(foundryBin, "forge"),
                Arguments = $"script script/Deploy.s.sol:Deploy --rpc-url {RpcUrl} --private-key {DeployerPrivateKey} --broadcast --slow",
                WorkingDirectory = BootstrapChainDir(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            }) ?? throw new InvalidOperationException("Failed to start forge script.");

            // Drain both pipes at once: reading one to the end first can deadlock on a full other pipe (#174).
            var stdoutTask = deploy.StandardOutput.ReadToEndAsync();
            var stderrTask = deploy.StandardError.ReadToEndAsync();
            await deploy.WaitForExitAsync();
            (stdout, stderr, exitCode) = (await stdoutTask, await stderrTask, deploy.ExitCode);
        }
        finally
        {
            RewardsChainFixture.ForgeGate.Release();
        }

        if (exitCode != 0)
        {
            throw new InvalidOperationException($"forge script deploy failed:\n{stdout}\n{stderr}");
        }

        NodeRegistryAddress = ExtractAddress(stdout, "NodeRegistry");
        EpochRootRegistryAddress = ExtractAddress(stdout, "EpochRootRegistry");
        ProtocolActivationsAddress = ExtractAddress(stdout, "ProtocolActivations");
    }

    private static string ExtractAddress(string output, string label)
    {
        var match = Regex.Match(output, $@"{label}:\s*(0x[a-fA-F0-9]{{40}})");
        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException($"Could not find {label} address in forge output:\n{output}");
    }

    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Gateway:RpcUrl"] = RpcUrl,
                ["Gateway:NodeRegistryAddress"] = NodeRegistryAddress,
                ["Gateway:EpochRootRegistryAddress"] = EpochRootRegistryAddress,
                ["Gateway:ProtocolActivationsAddress"] = ProtocolActivationsAddress,
                ["Gateway:RelayerPrivateKey"] = RelayerPrivateKey,
            });
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        await StopAnvilAsync();

        await base.DisposeAsync();
    }

    private async Task StopAnvilAsync()
    {
        if (_anvil is not null)
        {
            await _anvil.DisposeAsync();
            _anvil = null;
        }
    }
}

/// <summary>
/// One chain (one anvil and one forge deploy) shared by every class in the collection. Each extra
/// fixture is another concurrent forge run, which is what made these tests hang (#174/#176).
/// </summary>
[CollectionDefinition("ChainIntegration")]
public sealed class ChainIntegrationCollection : ICollectionFixture<ChainFixture>;
