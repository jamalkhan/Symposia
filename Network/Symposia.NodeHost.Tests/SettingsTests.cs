using Microsoft.Extensions.Configuration;
using Symposia.NodeHost.Components;
using Symposia.NodeHost.Diagnostics;
using Symposia.NodeHost.Settings;
using Symposia.NodeHost.Ui;

namespace Symposia.NodeHost.Tests;

/// <summary>Settings layering, secrets, presets, validation, and restart scoping (issue #133).</summary>
public sealed class SettingsTests : IDisposable
{
    private readonly TempDir _dir = new();

    private HostPaths Paths => new(_dir.Path);

    private static IConfiguration Env(params (string Key, string Value)[] values) =>
        new ConfigurationBuilder().AddInMemoryCollection(values.ToDictionary(v => v.Key, v => (string?)v.Value)).Build();

    // FR-A3: no settings file means an unconfigured host.
    [Fact]
    public void FreshHost_IsUnconfiguredWithOffsetPorts()
    {
        var store = new SettingsStore(Paths, portOffset: 2, Env());

        var s = store.Effective;

        Assert.Equal(NetworkMode.Unconfigured, s.Network.Mode);
        Assert.Equal(8547, s.Network.AnvilPort);
        Assert.Equal(8082, s.Network.GatewayPort);
        Assert.Equal(5182, s.Storage.Port);
        Assert.False(s.Storage.Enabled);
    }

    // TC-11: saved settings come back on restart.
    [Fact]
    public void SavedSettings_PersistAcrossRestart()
    {
        var store = new SettingsStore(Paths, 0, Env());
        var s = store.Effective;
        s.Network.Mode = NetworkMode.LocalDev;
        s.Storage.Enabled = true;
        store.Save(s);

        var reloaded = new SettingsStore(Paths, 0, Env()).Effective;

        Assert.Equal(NetworkMode.LocalDev, reloaded.Network.Mode);
        Assert.True(reloaded.Storage.Enabled);
    }

    // TC-14 / FR-E6: environment overrides win and are reported as locked.
    [Fact]
    public void EnvironmentOverride_WinsAndIsLocked()
    {
        var store = new SettingsStore(Paths, 0, Env(("Storage:Enabled", "true"), ("Storage:Port", "6000")));
        var s = store.Effective;
        s.Storage.Enabled = false;
        store.Save(s);

        Assert.True(store.Effective.Storage.Enabled);
        Assert.Equal(6000, store.Effective.Storage.Port);
        Assert.Equal(["Storage:Enabled", "Storage:Port"], store.LockedKeys);
    }

    // TC-3 / TC-13: password stored as a hash; secrets file owner-only.
    [Fact]
    public void Secrets_HashPasswordAndStayOwnerOnly()
    {
        var secrets = new SecretStore(Paths);
        secrets.SetPassword("correct horse");
        secrets.SetRelayerKey("0xabc");

        Assert.True(secrets.VerifyPassword("correct horse"));
        Assert.False(secrets.VerifyPassword("wrong"));
        Assert.DoesNotContain("correct horse", File.ReadAllText(Paths.Secrets));
        Assert.Equal("0xabc", secrets.RelayerPrivateKey);
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(Paths.Secrets));
        }
    }

    // TC-12 / FR-B3: base-sepolia ships undeployed; explicit fields complete it.
    [Fact]
    public void BaseSepoliaPreset_IsNotDeployedUntilAddressesAreSet()
    {
        var network = new NetworkSettings { Preset = "base-sepolia" };
        Assert.False(ChainPresets.Resolve(network).IsDeployed);
        Assert.Equal(84532UL, ChainPresets.Resolve(network).ChainId);

        network.NodeRegistryAddress = "0x5FbDB2315678afecb367f032d93F642f64180aa3";
        network.EpochRootRegistryAddress = "0xe7f1725E7734CE288F8367e1Bb143E90bb3F0512";
        Assert.True(ChainPresets.Resolve(network).IsDeployed);
    }

    [Fact]
    public void Validate_RejectsPortCollisionsAndBadEndpoints()
    {
        var s = HostSettings.Defaults(0);
        s.Network.Mode = NetworkMode.LocalDev;
        s.Storage.Enabled = true;
        s.Storage.Port = 8080;
        s.Compute.Enabled = true;
        s.Compute.AdvertisedEndpoint = "not a url";

        var errors = UiApp.Validate(s);

        Assert.Contains(errors, e => e.Contains("8080") && e.Contains("gateway") && e.Contains("storage"));
        Assert.Contains(errors, e => e.StartsWith("Compute advertised endpoint"));
    }

    [Fact]
    public void Validate_JoinNeedsGatewayUrl()
    {
        var s = HostSettings.Defaults(0);
        s.Network.Mode = NetworkMode.Join;
        s.Network.GatewayUrl = "";

        Assert.Contains(UiApp.Validate(s), e => e.Contains("gateway URL"));
    }

    // FR-D2 / FR-E3: a settings change restarts only what depends on it.
    [Theory]
    [InlineData(true, false, true, true, "Storage")]
    [InlineData(false, true, true, true, "Compute")]
    [InlineData(false, false, true, true, "BlobGateway")]
    [InlineData(false, false, true, false, "None")] // no secret: the gateway can't start, so it isn't registered
    [InlineData(false, false, false, true, "None")]
    public void PrimaryRole_StorageThenComputeThenARunnableGateway(bool storage, bool compute, bool gateway, bool secret, string expected)
    {
        var s = HostSettings.Defaults(0);
        (s.Storage.Enabled, s.Compute.Enabled, s.BlobGateway.Enabled, s.BlobGateway.AccessKeyId) = (storage, compute, gateway, "AK");

        Assert.Equal(expected, HostContext.PrimaryRoleFor(s, secret).ToString());
        Assert.Equal(expected == "BlobGateway" ? $"http://127.0.0.1:{s.BlobGateway.Port}" : null,
            expected == "BlobGateway" ? HostContext.PrimaryEndpointFor(s, secret) : expected == "None" ? HostContext.PrimaryEndpointFor(s, secret) : null);
    }

    [Fact]
    public void Supervisor_MovingThePrimary_RestartsOldAndNewPrimary()
    {
        var secrets = new SecretStore(Paths);
        secrets.SetBlobGatewaySecret("s3cret");
        var supervisor = new ComponentSupervisor(new HostContext(Paths, secrets, new LogBuffer()), new SettingsStore(Paths, 0, Env()));
        var gatewayOnly = HostSettings.Defaults(0);
        (gatewayOnly.BlobGateway.Enabled, gatewayOnly.BlobGateway.AccessKeyId) = (true, "AK");
        var withStorage = HostSettings.Defaults(0);
        (withStorage.BlobGateway.Enabled, withStorage.BlobGateway.AccessKeyId, withStorage.Storage.Enabled) = (true, "AK", true);

        var affected = supervisor.Affected(gatewayOnly, withStorage);

        Assert.Contains(supervisor.Network, affected);
        Assert.Contains(supervisor.Storage, affected);
        Assert.Contains(supervisor.BlobGateway, affected); // drops its handshake and directory
    }

    [Fact]
    public void Supervisor_RestartsOnlyAffectedComponents()
    {
        var supervisor = new ComponentSupervisor(new HostContext(Paths, new SecretStore(Paths), new LogBuffer()), new SettingsStore(Paths, 0, Env()));
        var before = HostSettings.Defaults(0);

        var storage = HostSettings.Defaults(0);
        storage.Storage.Enabled = true;
        Assert.Equal([supervisor.Network, supervisor.Storage], supervisor.Affected(before, storage));

        var discovery = HostSettings.Defaults(0);
        discovery.Discovery.IntervalSeconds = 30;
        Assert.Equal([supervisor.Network], supervisor.Affected(before, discovery));

        var network = HostSettings.Defaults(0);
        network.Network.Mode = NetworkMode.LocalDev;
        Assert.Equal(supervisor.All, supervisor.Affected(before, network));

        Assert.Empty(supervisor.Affected(before, HostSettings.Defaults(0)));

        // Blob gateway (issue #135): its own settings restart only it; with it on, so does Discovery.
        var gateway = HostSettings.Defaults(0);
        gateway.BlobGateway.Enabled = true;
        Assert.Equal([supervisor.BlobGateway], supervisor.Affected(before, gateway));

        var gatewayDiscovery = HostSettings.Defaults(0);
        gatewayDiscovery.BlobGateway.Enabled = true;
        var gatewayDiscoveryNext = HostSettings.Defaults(0);
        gatewayDiscoveryNext.BlobGateway.Enabled = true;
        gatewayDiscoveryNext.Discovery.IntervalSeconds = 30;
        Assert.Equal([supervisor.Network, supervisor.BlobGateway], supervisor.Affected(gatewayDiscovery, gatewayDiscoveryNext));

        // #225: the primary role serves the signed service directory listing the gateway, so it restarts too.
        var withStorage = HostSettings.Defaults(0);
        withStorage.Storage.Enabled = true;
        var withStorageGateway = HostSettings.Defaults(0);
        withStorageGateway.Storage.Enabled = true;
        withStorageGateway.BlobGateway.Enabled = true;
        Assert.Equal([supervisor.BlobGateway, supervisor.Storage], supervisor.Affected(withStorage, withStorageGateway).OrderBy(c => c.Name));

        var verifier = HostSettings.Defaults(0);
        verifier.Verifier.Enabled = true;
        Assert.Equal([supervisor.Verifier], supervisor.Affected(before, verifier));

        // Review (Carl 5): a change that doesn't move the primary endpoint leaves the network alone.
        var capacity = HostSettings.Defaults(0);
        capacity.Storage.Enabled = true;
        var bigger = HostSettings.Defaults(0);
        bigger.Storage.Enabled = true;
        bigger.Storage.MaxCapacityBytes *= 2;
        Assert.Equal([supervisor.Storage], supervisor.Affected(capacity, bigger));
    }

    // FR-D3: the registered endpoint is storage's when enabled, else compute's.
    [Fact]
    public void PrimaryEndpoint_PrefersStorageThenCompute()
    {
        var context = new HostContext(Paths, new SecretStore(Paths), new LogBuffer()) { Settings = HostSettings.Defaults(0) };
        Assert.Null(context.PrimaryEndpoint);

        context.Settings.Compute.Enabled = true;
        Assert.Equal("http://127.0.0.1:5190", context.PrimaryEndpoint);

        context.Settings.Storage.Enabled = true;
        context.Settings.Storage.AdvertisedEndpoint = "http://10.0.0.5:5180";
        Assert.Equal("http://10.0.0.5:5180", context.PrimaryEndpoint);
    }

    // Review (Frank 3): role data is scoped to the network it belongs to.
    [Fact]
    public void RoleDataDir_IsScopedToNetwork()
    {
        var context = new HostContext(Paths, new SecretStore(Paths), new LogBuffer())
        {
            Network = new NetworkInfo("http://gw", 31337, "0x5FbDB2315678afecb367f032d93F642f64180aa3", "0xe7f1", null),
        };
        var local = context.RoleDataDir("storage");

        context.Network = context.Network with { ChainId = 84532 };

        Assert.EndsWith("31337-5fbdb231", local);
        Assert.NotEqual(local, context.RoleDataDir("storage"));
    }

    // Review (Lenny 8): a corrupt settings file is reported, never silently reset.
    [Fact]
    public void CorruptSettings_Throws()
    {
        File.WriteAllText(Paths.Settings, "{ not json");

        Assert.Throws<CorruptFileException>(() => new SettingsStore(Paths, 0, Env()));
    }

    // Issue #135: ports added after a settings file was written get this host's offset defaults.
    [Fact]
    public void OlderSettingsFile_GetsOffsetDefaultsForNewPorts()
    {
        Directory.CreateDirectory(Paths.DataDir);
        File.WriteAllText(Paths.Settings, """{ "storage": { "enabled": true, "port": 5181 } }""");

        var settings = new SettingsStore(Paths, 1, Env()).Effective;

        Assert.Equal(6181, settings.Storage.GrpcPort);
        Assert.Equal(6281, settings.Storage.GrpcTlsPort);
        Assert.True(settings.Storage.PlaintextGrpc); // #145 overlap: serve the previous release's gateways too
        Assert.Equal(9001, settings.BlobGateway.Port);
    }

    // Issue #145: the TLS and cleartext gRPC ports must not clash; cleartext stops counting once turned off.
    [Fact]
    public void Validate_ChecksTlsGrpcPort_AndIgnoresCleartextPortWhenOff()
    {
        var s = HostSettings.Defaults(0);
        s.Network.Mode = NetworkMode.LocalDev;
        s.Storage.Enabled = true;
        s.Storage.GrpcTlsPort = s.Storage.GrpcPort;
        Assert.Contains(UiApp.Validate(s), e => e.Contains("storage gRPC (TLS)") && e.Contains("storage gRPC"));

        s.Storage.PlaintextGrpc = false;
        Assert.DoesNotContain(UiApp.Validate(s), e => e.Contains("storage gRPC (TLS)"));
    }

    [Fact]
    public void Validate_RejectsGatewayQuorumAboveMinCopies_AndPortClashes()
    {
        var s = HostSettings.Defaults(0);
        s.Network.Mode = NetworkMode.LocalDev;
        s.Storage.Enabled = true;
        s.BlobGateway.Enabled = true;
        s.BlobGateway.WriteQuorum = 3;
        s.BlobGateway.MinCopies = 2;
        s.BlobGateway.Port = s.Storage.GrpcPort;

        var errors = UiApp.Validate(s);

        Assert.Contains(errors, e => e.Contains("write quorum"));
        Assert.Contains(errors, e => e.Contains("storage gRPC") && e.Contains("blob gateway"));
    }

    public void Dispose() => _dir.Dispose();
}
