using System.Diagnostics;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

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

    private Process? _anvil;
    private readonly int _port = GetFreePort();

    public string RpcUrl => $"http://127.0.0.1:{_port}";
    public string NodeRegistryAddress { get; private set; } = "";
    public string EpochRootRegistryAddress { get; private set; } = "";
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

    private static int GetFreePort()
    {
        using var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    public async Task InitializeAsync()
    {
        var foundryBin = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "bin");

        _anvil = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(foundryBin, "anvil"),
            Arguments = $"--port {_port} --silent",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start anvil.");

        await WaitForRpcAsync();

        var deploy = Process.Start(new ProcessStartInfo
        {
            FileName = Path.Combine(foundryBin, "forge"),
            Arguments = $"script script/Deploy.s.sol:Deploy --rpc-url {RpcUrl} --private-key {DeployerPrivateKey} --broadcast",
            WorkingDirectory = BootstrapChainDir(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("Failed to start forge script.");

        var stdout = await deploy.StandardOutput.ReadToEndAsync();
        var stderr = await deploy.StandardError.ReadToEndAsync();
        await deploy.WaitForExitAsync();

        if (deploy.ExitCode != 0)
        {
            throw new InvalidOperationException($"forge script deploy failed:\n{stdout}\n{stderr}");
        }

        NodeRegistryAddress = ExtractAddress(stdout, "NodeRegistry");
        EpochRootRegistryAddress = ExtractAddress(stdout, "EpochRootRegistry");
    }

    private static string ExtractAddress(string output, string label)
    {
        var match = Regex.Match(output, $@"{label}:\s*(0x[a-fA-F0-9]{{40}})");
        return match.Success
            ? match.Groups[1].Value
            : throw new InvalidOperationException($"Could not find {label} address in forge output:\n{output}");
    }

    private async Task WaitForRpcAsync()
    {
        using var http = new HttpClient();
        for (var i = 0; i < 100; i++)
        {
            try
            {
                var resp = await http.PostAsync(RpcUrl,
                    new StringContent("""{"jsonrpc":"2.0","method":"eth_chainId","params":[],"id":1}""",
                        System.Text.Encoding.UTF8, "application/json"));
                if (resp.IsSuccessStatusCode)
                {
                    return;
                }
            }
            catch
            {
                // anvil not up yet, retry.
            }

            await Task.Delay(100);
        }

        throw new InvalidOperationException("anvil did not become reachable in time.");
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
                ["Gateway:RelayerPrivateKey"] = RelayerPrivateKey,
            });
        });
    }

    async Task IAsyncLifetime.DisposeAsync()
    {
        if (_anvil is { HasExited: false })
        {
            _anvil.Kill(entireProcessTree: true);
            await _anvil.WaitForExitAsync();
        }

        _anvil?.Dispose();
        await base.DisposeAsync();
    }
}
