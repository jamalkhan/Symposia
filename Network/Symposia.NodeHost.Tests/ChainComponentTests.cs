using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
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

    private static string FoundryBin => Environment.GetEnvironmentVariable("FOUNDRY_BIN")
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "bin");

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    /// <summary>Starts anvil exactly as LocalDev does (plus <paramref name="extra"/>) and waits until it answers.</summary>
    private static async Task<Process> StartLocalDevAnvilAsync(HttpClient http, int port, string statePath, params string[] extra)
    {
        var start = new ProcessStartInfo(Path.Combine(FoundryBin, "anvil")) { UseShellExecute = false };
        foreach (var arg in ChainComponent.AnvilArguments(port, statePath).Concat(extra))
        {
            start.ArgumentList.Add(arg);
        }

        var anvil = Process.Start(start)!;
        for (var i = 0; i < 100; i++)
        {
            if (await RpcAsync(http, port, "eth_blockNumber", "[]") is not null)
            {
                return anvil;
            }

            await Task.Delay(100);
        }

        await StopAsync(anvil);
        throw new Xunit.Sdk.XunitException($"anvil never answered on port {port}");
    }

    /// <summary>The JSON-RPC result, or null if anvil isn't listening.</summary>
    private static async Task<JsonElement?> RpcAsync(HttpClient http, int port, string method, string parameters)
    {
        try
        {
            using var response = await http.PostAsync($"http://127.0.0.1:{port}", new StringContent(
                $$"""{"jsonrpc":"2.0","id":1,"method":"{{method}}","params":{{parameters}}}""", Encoding.UTF8, "application/json"));
            return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("result").Clone();
        }
        catch (HttpRequestException)
        {
            return null;
        }
    }

    private static async Task StopAsync(Process anvil)
    {
        if (!anvil.HasExited)
        {
            anvil.Kill(entireProcessTree: true);
            await anvil.WaitForExitAsync();
        }

        anvil.Dispose();
    }

    // #193: with no transactions at all, LocalDev's anvil still mines, so the chain clock (and the epoch) moves.
    [Fact]
    public async Task LocalDevAnvil_MinesWithoutTransactions()
    {
        using var http = new HttpClient();
        var port = FreePort();
        var anvil = await StartLocalDevAnvilAsync(http, port, Path.Combine(_dir.Path, "anvil.json"));
        try
        {
            async Task<(long Number, long Timestamp)> HeadAsync()
            {
                var block = await RpcAsync(http, port, "eth_getBlockByNumber", """["latest",false]""");
                Assert.NotNull(block);
                return (Convert.ToInt64(block.Value.GetProperty("number").GetString(), 16), Convert.ToInt64(block.Value.GetProperty("timestamp").GetString(), 16));
            }

            var first = await HeadAsync();
            await Task.Delay(TimeSpan.FromSeconds(5));
            var later = await HeadAsync();

            Assert.True(later.Number >= first.Number + 2, $"anvil mined {later.Number - first.Number} blocks in 5 s");
            Assert.True(later.Timestamp >= first.Timestamp + 4, "the chain clock did not advance");
        }
        finally
        {
            await StopAsync(anvil);
        }
    }

    // #210: with --prune-history, a burst of blocks spills no history snapshots, and --state still carries the
    // chain across a restart. --cache-path isolates this anvil's snapshot dir from every other anvil on the machine.
    [Fact]
    public async Task LocalDevAnvil_PrunesHistoryAndKeepsStateAcrossRestart()
    {
        if (OperatingSystem.IsWindows())
        {
            return; // No SIGINT to request the --state dump on exit.
        }

        const string address = "0x00000000000000000000000000000000000d2100";
        using var http = new HttpClient();
        var port = FreePort();
        var statePath = Path.Combine(_dir.Path, "anvil.json");
        var cache = Path.Combine(_dir.Path, "anvil-cache");
        string[] Snapshots() => Directory.Exists(cache) ? Directory.GetFileSystemEntries(cache, "*", SearchOption.AllDirectories) : [];

        var anvil = await StartLocalDevAnvilAsync(http, port, statePath, "--cache-path", cache);
        try
        {
            Assert.NotNull(await RpcAsync(http, port, "anvil_setCode", $"""["{address}","0x6001"]"""));
            Assert.NotNull(await RpcAsync(http, port, "anvil_mine", """["0x7d0"]"""));
            var height = Convert.ToInt64((await RpcAsync(http, port, "eth_blockNumber", "[]"))?.GetString(), 16);
            Assert.True(height >= 2000, $"only {height} blocks mined");
            Assert.Empty(Snapshots());

            // Clean exit, as ChainComponent.OnStopAsync does, so anvil dumps --state.
            using (var signal = Process.Start("kill", ["-INT", anvil.Id.ToString()]))
            {
                await signal.WaitForExitAsync();
            }

            using (var wait = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            {
                await anvil.WaitForExitAsync(wait.Token);
            }

            await StopAsync(anvil);
            anvil = await StartLocalDevAnvilAsync(http, port, statePath, "--cache-path", cache);
            Assert.Equal("0x6001", (await RpcAsync(http, port, "eth_getCode", $"""["{address}","latest"]"""))?.GetString());
        }
        finally
        {
            await StopAsync(anvil);
        }
    }

    // #191: LocalDev reads the committee address from the key Program.cs already wrote, without a loaded identity.
    [Fact]
    public void LocalNodeAddress_LoadsTheExistingNodeKey()
    {
        var paths = new HostPaths(_dir.Path);
        Directory.CreateDirectory(paths.DataDir);
        var identity = new Symposia.Network.Node.NodeIdentity(Microsoft.Extensions.Options.Options.Create(
            new Symposia.Network.Node.NodeNetworkOptions { NodeIdentityKeyPath = paths.NodeKey }));
        identity.EnsureLoadedOrGenerated();

        Assert.Equal(identity.NodeId, ChainComponent.LocalNodeAddress(Context(HostSettings.Defaults(0))));
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
