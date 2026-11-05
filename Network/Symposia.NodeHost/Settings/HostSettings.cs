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

    public LoggingSettings Logging { get; set; } = new();

    public BlobGatewaySettings BlobGateway { get; set; } = new();

    public static HostSettings Defaults(int portOffset) => new()
    {
        Network = new NetworkSettings
        {
            AnvilPort = 8545 + portOffset,
            GatewayPort = 8080 + portOffset,
            GatewayUrl = "http://127.0.0.1:8080",
        },
        Storage = new StorageSettings { Port = 5180 + portOffset, GrpcPort = 6180 + portOffset, GrpcTlsPort = 6280 + portOffset },
        Compute = new ComputeSettings { Port = 5190 + portOffset },
        BlobGateway = new BlobGatewaySettings { Port = 9000 + portOffset },
    };

    /// <summary>
    /// Fills ports added after a settings file was first written (0 = never set) with this
    /// host's offset defaults, so two hosts upgraded in place don't both pick the same port.
    /// </summary>
    public HostSettings WithNewDefaults(int portOffset)
    {
        var defaults = Defaults(portOffset);
        if (Storage.GrpcPort == 0)
        {
            Storage.GrpcPort = defaults.Storage.GrpcPort;
        }

        if (Storage.GrpcTlsPort == 0)
        {
            Storage.GrpcTlsPort = defaults.Storage.GrpcTlsPort;
        }

        if (BlobGateway.Port == 0)
        {
            BlobGateway.Port = defaults.BlobGateway.Port;
        }

        return this;
    }
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

    /// <summary>HTTP/2-only cleartext port for the storage gRPC API (issue #135); served while <see cref="PlaintextGrpc"/> is on.</summary>
    public int GrpcPort { get; set; }

    /// <summary>Port for the storage gRPC API over TLS, which gateways prefer and pin (issues #142, #145).</summary>
    public int GrpcTlsPort { get; set; }

    /// <summary>
    /// Also serve storage gRPC in cleartext, so blob gateways from the previous release can still reach
    /// this node (the #145 overlap). Turn off once <c>storage.grpc.tls</c> is activated on chain.
    /// </summary>
    public bool PlaintextGrpc { get; set; } = true;
}

/// <summary>
/// The S3-compatible blob gateway role (issue #135): accepts uploads with AWS-style keys and
/// stores them on the network's storage nodes, found through the on-chain registry. One
/// operator-set key for now; the secret lives in the secret store, not here (#3 adds real tenants).
/// </summary>
public sealed class BlobGatewaySettings
{
    public bool Enabled { get; set; }

    public int Port { get; set; }

    public string Region { get; set; } = "us-east-1";

    public string AccessKeyId { get; set; } = "";

    /// <summary>Uploads succeed once this many storage nodes have the object.</summary>
    public int WriteQuorum { get; set; } = 1;

    /// <summary>Copies every object should end up with; missing ones are repaired in the background.</summary>
    public int MinCopies { get; set; } = 2;
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

/// <summary>
/// Minimum log level per service. Each service writes its own rolling files under
/// &lt;data-dir&gt;/logs/&lt;service&gt;/. "Node" covers everything that isn't the storage
/// or compute role: chain, gateway, network (registration/discovery), the UI and the host.
/// Applied live; no restart needed.
/// </summary>
public sealed class LoggingSettings
{
    public Microsoft.Extensions.Logging.LogLevel Node { get; set; } = Microsoft.Extensions.Logging.LogLevel.Information;

    public Microsoft.Extensions.Logging.LogLevel Storage { get; set; } = Microsoft.Extensions.Logging.LogLevel.Information;

    public Microsoft.Extensions.Logging.LogLevel Compute { get; set; } = Microsoft.Extensions.Logging.LogLevel.Information;
}
