using System.Diagnostics;
using System.Text.Json;
using Symposia.Blockchain.Gateway.Chain;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// A disposable anvil chain with the node-rewards stack deployed by
/// <c>script/DeployRewards.s.sol</c> (roles default to the deployer, anvil account #0).
/// </summary>
public sealed class RewardsChainFixture : IAsyncLifetime
{
    public const string DeployerKey = "0xac0974bec39a17e36ba4a6b4d238ff944bacb478cbed5efcae784d7bf4f2ff80";
    public const string NodeKey = "0x59c6995e998f97a5a0044966f0945389dc9e86dae88c7a8412f4603b6b78690d"; // anvil account #1

    private static readonly string FoundryBin = AnvilProcess.FoundryBin;

    /// <summary>
    /// One forge invocation at a time across the test process: on a fresh checkout, several fixtures
    /// (and ContractAbiTests) compiling the same project at once contend over out/ and the cache, and
    /// one can stall indefinitely (#176, after #174 turned the hang into a timeout).
    /// </summary>
    public static readonly SemaphoreSlim ForgeGate = new(1, 1);

    private AnvilProcess? _anvil;
    // Foundry's fs_permissions only allow the script to write under bootstrap-chain/deployments.
    private readonly string _out = Path.Combine(BootstrapChainDir(), "deployments", $"test-{Guid.NewGuid():N}.json");

    public string RpcUrl => _anvil?.RpcUrl ?? throw new InvalidOperationException("anvil has not started.");

    public IReadOnlyDictionary<string, string> Addresses { get; private set; } = new Dictionary<string, string>();

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
        _anvil = await AnvilProcess.StartAsync();

        var deploy = new ProcessStartInfo(Path.Combine(FoundryBin, "forge"),
            $"script script/DeployRewards.s.sol:DeployRewards --rpc-url {RpcUrl} --private-key {DeployerKey} --broadcast")
        {
            WorkingDirectory = BootstrapChainDir(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            Environment = { ["DEPLOYMENT_OUT"] = _out },
        };
        await ForgeGate.WaitAsync();
        try
        {
            await RunDeployAsync(deploy);
        }
        finally
        {
            ForgeGate.Release();
        }

        using var json = JsonDocument.Parse(await File.ReadAllTextAsync(_out));
        Addresses = json.RootElement.EnumerateObject()
            .Where(p => p.Value.ValueKind == JsonValueKind.String)
            .ToDictionary(p => p.Name, p => p.Value.GetString()!);
    }

    private static async Task RunDeployAsync(ProcessStartInfo deploy)
    {
        using var forge = Process.Start(deploy) ?? throw new InvalidOperationException("Failed to start forge.");
        var stdout = forge.StandardOutput.ReadToEndAsync();
        var stderrTask = forge.StandardError.ReadToEndAsync();
        using (var limit = new CancellationTokenSource(TimeSpan.FromMinutes(5)))
        {
            try
            {
                await forge.WaitForExitAsync(limit.Token);
            }
            catch (OperationCanceledException)
            {
                forge.Kill(entireProcessTree: true);
                throw new TimeoutException("DeployRewards did not finish within 5 minutes.");
            }
        }

        var stderr = await stderrTask;
        if (forge.ExitCode != 0)
        {
            throw new InvalidOperationException($"DeployRewards failed:\n{await stdout}\n{stderr}");
        }

    }

    public async Task DisposeAsync()
    {
        await StopAnvilAsync();

        File.Delete(_out);
    }

    private static string BootstrapChainDir()
    {
        var dir = AppContext.BaseDirectory;
        while (dir is not null && !Directory.Exists(Path.Combine(dir, "Blockchain", "bootstrap-chain")))
        {
            dir = Directory.GetParent(dir)?.FullName;
        }

        return dir is null
            ? throw new InvalidOperationException("Could not locate Blockchain/bootstrap-chain.")
            : Path.Combine(dir, "Blockchain", "bootstrap-chain");
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
