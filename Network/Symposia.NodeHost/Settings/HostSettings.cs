namespace Symposia.NodeHost.Settings;

/// <summary>How this host participates in a network (issue #133 arch plan).</summary>
public enum NetworkMode
{
    /// <summary>Not configured yet: only the config UI runs (FR-A3).</summary>
    Unconfigured,

    /// <summary>Developer machine: start anvil, deploy contracts, run a local gateway.</summary>
    LocalDev,

    /// <summary>Join an existing network knowing only its gateway URL.</summary>
    Join,

    /// <summary>Run a local gateway against a remote EVM chain (preset), with a relayer key.</summary>
    RemoteRpc,
}

/// <summary>Operator-editable settings, persisted to settings.json (FR-E5).</summary>
public sealed class HostSettings
{
    public NetworkSettings Network { get; set; } = new();

    public StorageSettings Storage { get; set; } = new();

    public ComputeSettings Compute { get; set; } = new();

    public DiscoverySettings Discovery { get; set; } = new();

    public static HostSettings Defaults(int portOffset) => new()
    {
        Network = new NetworkSettings
        {
            AnvilPort = 8545 + portOffset,
            GatewayPort = 8080 + portOffset,
            GatewayUrl = "http://127.0.0.1:8080",
        },
        Storage = new StorageSettings { Port = 5180 + portOffset },
        Compute = new ComputeSettings { Port = 5190 + portOffset },
    };
}

public sealed class NetworkSettings
{
    public NetworkMode Mode { get; set; } = NetworkMode.Unconfigured;

    /// <summary>Join mode: the network's gateway.</summary>
    public string GatewayUrl { get; set; } = "";

    /// <summary>RemoteRpc mode: named chain preset; explicit fields below override it.</summary>
    public string Preset { get; set; } = "base-sepolia";

    public string RpcUrl { get; set; } = "";

    public ulong ChainId { get; set; }

    public string NodeRegistryAddress { get; set; } = "";

    public string EpochRootRegistryAddress { get; set; } = "";

    public int AnvilPort { get; set; } = 8545;

    public int GatewayPort { get; set; } = 8080;

    /// <summary>Directory holding anvil/forge; empty = ~/.foundry/bin, then PATH.</summary>
    public string FoundryBin { get; set; } = "";
}

public sealed class StorageSettings
{
    public bool Enabled { get; set; }

    public int Port { get; set; } = 5180;

    /// <summary>Base URL peers use to reach this role; empty = http://127.0.0.1:{Port}.</summary>
    public string AdvertisedEndpoint { get; set; } = "";

    public long MaxCapacityBytes { get; set; } = 10L * 1024 * 1024 * 1024;
}

public sealed class ComputeSettings
{
    public bool Enabled { get; set; }

    public int Port { get; set; } = 5190;

    public string AdvertisedEndpoint { get; set; } = "";

    /// <summary>Stand in stub processes for the Neon binaries so only networking is exercised.</summary>
    public bool NetworkingOnly { get; set; }

    public string PageserverPath { get; set; } = "pageserver";

    public string SafekeeperPath { get; set; } = "safekeeper";

    public string PostgresPath { get; set; } = "postgres";
}

public sealed class DiscoverySettings
{
    public int IntervalSeconds { get; set; } = 10;

    public string[] SeedPeers { get; set; } = [];
}
