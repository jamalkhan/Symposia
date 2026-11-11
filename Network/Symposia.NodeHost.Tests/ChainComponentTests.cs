using Symposia.NodeHost.Components;
using Symposia.NodeHost.Diagnostics;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Tests;

[CollectionDefinition(nameof(ProcessEnvironment), DisableParallelization = true)]
public sealed class ProcessEnvironment;

/// <summary>Chain component fault paths (issue #133 FR-A4, FR-B2; QA TC-12, TC-15). Not parallel: one test rewrites PATH/HOME.</summary>
[Collection(nameof(ProcessEnvironment))]
public sealed class ChainComponentTests : IDisposable
{
    private readonly TempDir _dir = new();

    private HostContext Context(HostSettings settings) =>
        new(new HostPaths(_dir.Path), new SecretStore(new HostPaths(_dir.Path)), new LogBuffer()) { Settings = settings };

    // #193: with no transactions at all, LocalDev's anvil still mines, so the chain clock (and the epoch) moves.
    [Fact]
    public async Task LocalDevAnvil_MinesWithoutTransactions()
    {
        var foundry = Environment.GetEnvironmentVariable("FOUNDRY_BIN")
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "bin");
        using var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        probe.Start();
        var port = ((System.Net.IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(foundry, "anvil")) { UseShellExecute = false };
        foreach (var arg in ChainComponent.AnvilArguments(port, Path.Combine(_dir.Path, "anvil.json")))
        {
            start.ArgumentList.Add(arg);
        }

        using var anvil = System.Diagnostics.Process.Start(start)!;
        try
        {
            using var http = new HttpClient();
            async Task<(long Number, long Timestamp)?> HeadAsync()
            {
                try
                {
                    using var response = await http.PostAsync($"http://127.0.0.1:{port}", new StringContent(
                        """{"jsonrpc":"2.0","id":1,"method":"eth_getBlockByNumber","params":["latest",false]}""",
                        System.Text.Encoding.UTF8, "application/json"));
                    var block = System.Text.Json.JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result");
                    return (Convert.ToInt64(block.GetProperty("number").GetString(), 16), Convert.ToInt64(block.GetProperty("timestamp").GetString(), 16));
                }
                catch (HttpRequestException)
                {
                    return null;
                }
            }

            (long Number, long Timestamp)? first = null;
            for (var i = 0; i < 100 && first is null; i++)
            {
                first = await HeadAsync();
                if (first is null)
                {
                    await Task.Delay(100);
                }
            }

            Assert.NotNull(first);
            await Task.Delay(TimeSpan.FromSeconds(5));
            var later = await HeadAsync();

            Assert.NotNull(later);
            Assert.True(later.Value.Number >= first.Value.Number + 2, $"anvil mined {later.Value.Number - first.Value.Number} blocks in 5 s");
            Assert.True(later.Value.Timestamp >= first.Value.Timestamp + 4, "the chain clock did not advance");
        }
        finally
        {
            anvil.Kill(entireProcessTree: true);
            await anvil.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task RemoteRpc_UndeployedPreset_FaultsWithoutNetwork()
    {
        var settings = HostSettings.Defaults(0);
        settings.Network.Mode = NetworkMode.RemoteRpc;
        settings.Network.Preset = "base-sepolia";
        var context = Context(settings);
        var chain = new ChainComponent();

        await chain.StartAsync(context, CancellationToken.None);

        Assert.Equal(ComponentStatus.Faulted, chain.Status);
        Assert.Contains("not yet deployed", chain.Reason);
        Assert.Null(context.Network);
    }

    [Fact]
    public async Task RemoteRpc_DeployedPresetWithoutRelayerKey_Faults()
    {
        var settings = HostSettings.Defaults(0);
        settings.Network.Mode = NetworkMode.RemoteRpc;
        settings.Network.NodeRegistryAddress = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
        settings.Network.EpochRootRegistryAddress = "0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512";
        var chain = new ChainComponent();

        await chain.StartAsync(Context(settings), CancellationToken.None);

        Assert.Contains("relayer key", chain.Reason);
    }

    [Fact]
    public async Task Join_UnreachableGateway_Faults()
    {
        var settings = HostSettings.Defaults(0);
        settings.Network.Mode = NetworkMode.Join;
        settings.Network.GatewayUrl = "http://127.0.0.1:1";
        var chain = new ChainComponent();

        await chain.StartAsync(Context(settings), CancellationToken.None);

        Assert.Equal(ComponentStatus.Faulted, chain.Status);
        Assert.Contains("Could not reach the gateway", chain.Reason);
    }

    [Fact]
    public async Task Unconfigured_IsDisabled()
    {
        var chain = new ChainComponent();

        await chain.StartAsync(Context(HostSettings.Defaults(0)), CancellationToken.None);

        Assert.Equal(ComponentStatus.Disabled, chain.Status);
    }

    // TC-15: missing Foundry is a clear fault, not a crash.
    [Fact]
    public async Task LocalDev_FoundryMissing_FaultsWithInstallHint()
    {
        var settings = HostSettings.Defaults(0);
        settings.Network.Mode = NetworkMode.LocalDev;
        settings.Network.AnvilPort = 1;
        settings.Network.FoundryBin = _dir.Path;
        var originalPath = Environment.GetEnvironmentVariable("PATH");
        var originalHome = Environment.GetEnvironmentVariable("HOME");
        Environment.SetEnvironmentVariable("PATH", _dir.Path);
        Environment.SetEnvironmentVariable("HOME", _dir.Path);
        try
        {
            var chain = new ChainComponent();
            await chain.StartAsync(Context(settings), CancellationToken.None);

            Assert.Equal(ComponentStatus.Faulted, chain.Status);
            Assert.Contains("Install Foundry", chain.Reason);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            Environment.SetEnvironmentVariable("HOME", originalHome);
        }
    }

    public void Dispose() => _dir.Dispose();
}
