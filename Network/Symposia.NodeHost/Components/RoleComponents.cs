using Symposia.BlobStorage.Gateway;
using Symposia.BlobStorage.StorageNode;
using Symposia.Database.ComputeNode;
using Symposia.NodeHost.Diagnostics;

namespace Symposia.NodeHost.Components;

/// <summary>Node-network settings every hosted role shares. Roles only answer handshakes; the host registers and discovers once.</summary>
internal static class RoleNetwork
{
    public static void Add(IDictionary<string, string?> config, string section, HostContext context, string advertisedEndpoint)
    {
        var network = context.Network!;
        config[$"{section}:NodeIdentityKeyPath"] = context.Paths.NodeKey;
        config[$"{section}:BlockchainGatewayUrl"] = network.GatewayUrl;
        config[$"{section}:NodeRegistryAddress"] = network.NodeRegistryAddress;
        config[$"{section}:ChainId"] = network.ChainId.ToString();
        config[$"{section}:AdvertisedEndpoint"] = advertisedEndpoint;
        config[$"{section}:EnableRegistration"] = "false";
        config[$"{section}:EnableDiscovery"] = "false";
    }
}

/// <summary>The storage node role (FR-D1).</summary>
public sealed class StorageRoleComponent : WebAppComponent
{
    public override string Name => "storage";

    protected override bool UsesRoleDataDir => true;

    protected override bool ServesNodeHealth => true;

    protected override bool BindsOwnPort => true;

    protected override string? SkipReason(HostContext context) =>
        !context.Settings.Storage.Enabled ? "Storage service is turned off" : base.SkipReason(context);

    protected override WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder> configure, HostContext context) =>
        StorageNodeApp.Create(options, builder =>
        {
            configure(builder);
            // Heartbeat acceptance surfaces on the host's Diagnostics page (#181 FR5); each start is a fresh run.
            context.Telemetry.Reset();
            builder.Services.AddSingleton(context.Telemetry);
            builder.Services.AddSingleton<Symposia.Network.Node.Alerts.IOperatorAlertSink>(context.Alerts);
        });

    protected override int Port(HostContext context) => context.Settings.Storage.Port;

    // Storage stake scales per TB offered, rounded up (tokenomics-mvp §6: "per TB offered (ceil)").
    protected override (byte Service, long Capacity)? StakedService(HostContext context) =>
        (0, (context.Settings.Storage.MaxCapacityBytes + StakeUnitBytes - 1) / StakeUnitBytes);

    private const long StakeUnitBytes = 1_000_000_000_000;

    protected override IDictionary<string, string?> Configuration(HostContext context)
    {
        var storage = context.Settings.Storage;
        var dir = context.RoleDataDir(Name);
        var config = new Dictionary<string, string?>
        {
            ["StorageNode:ListenPort"] = storage.Port.ToString(),
            // Cleartext gRPC only during the overlap with the previous release (issue #145).
            ["StorageNode:GrpcPort"] = storage.PlaintextGrpc ? storage.GrpcPort.ToString() : "0",
            // Only registered members of this network may call the storage API (issue #140).
            ["StorageNode:RequireCallerAuth"] = "true",
            // gRPC over TLS with a node-key-bound certificate, pinned by gateways (issue #142).
            ["StorageNode:GrpcTlsPort"] = storage.GrpcTlsPort.ToString(),
            ["StorageNode:TlsCertificatePath"] = Path.Combine(dir, "grpc-tls.pfx"),
            ["StorageNode:StorageRoot"] = Path.Combine(dir, "blobs"),
            ["StorageNode:ManifestDbPath"] = Path.Combine(dir, "manifest.db"),
            ["StorageNode:MaxCapacityBytes"] = storage.MaxCapacityBytes.ToString(),
            // 5-minute signed heartbeats that feed reward scoring (issue #75).
            ["StorageNode:Telemetry:Enabled"] = storage.Heartbeats.ToString(),
        };
        var network = context.Network!;
        if (!string.IsNullOrEmpty(network.TelemetryRegistryAddress) && !string.IsNullOrEmpty(network.RpcUrl) && storage.Heartbeats)
        {
            // HTTP gossip, the self-checkpoint fallback and committee duties (#158); the schedule syncs from the EpochController.
            config["StorageNode:Telemetry:Chain:RpcUrl"] = network.RpcUrl;
            config["StorageNode:Telemetry:Chain:TelemetryRegistryAddress"] = network.TelemetryRegistryAddress;
            config["StorageNode:Telemetry:Chain:EpochControllerAddress"] = network.EpochControllerAddress;
            // Log scans start at the rewards deployment and page under capped RPCs (#172).
            config["StorageNode:Telemetry:Chain:FromBlock"] = network.DeployBlock.ToString();
            if (network.MaxLogBlockRange is > 0 and var range)
            {
                config["StorageNode:Telemetry:Chain:MaxLogBlockRange"] = range.ToString();
            }
            config["StorageNode:Telemetry:Blobs:CacheDirectory"] = Path.Combine(dir, "telemetry-blobs");
        }

        RoleNetwork.Add(config, "StorageNode", context, HostContext.EndpointFor(storage.AdvertisedEndpoint, storage.Port));
        return config;
    }
}

/// <summary>The compute node role (FR-D1), optionally with stub Neon binaries for networking-only testing.</summary>
public sealed class ComputeRoleComponent : WebAppComponent
{
    public override string Name => "compute";

    protected override bool UsesRoleDataDir => true;

    protected override bool ServesNodeHealth => true;

    protected override string? SkipReason(HostContext context) =>
        !context.Settings.Compute.Enabled ? "Compute service is turned off" : base.SkipReason(context);

    protected override WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder> configure, HostContext context)
    {
        try
        {
            return ComputeNodeApp.Create(options, configure);
        }
        catch (System.ComponentModel.Win32Exception ex)
        {
            throw new ComponentFaultException(
                $"A Neon binary could not be started ({ex.Message}). Set the binary paths, or turn on 'networking only' to test without them.");
        }
    }

    protected override int Port(HostContext context) => context.Settings.Compute.Port;

    /// <summary>
    /// The compute control API (placement, deletion, internal proxy/safekeeper routes) is
    /// loopback/orchestration-only and unauthenticated (see ComputeNodeApp), but the
    /// listener must be reachable for peer handshakes. So off-box callers get only the
    /// handshake endpoint.
    /// </summary>
    protected override void Secure(WebApplication app) =>
        app.Use(async (http, next) =>
        {
            var remote = http.Connection.RemoteIpAddress;
            if (remote is not null && !System.Net.IPAddress.IsLoopback(remote) && http.Request.Path != "/v1/peer/handshake" && http.Request.Path != NodeHealth.Path)
            {
                http.Response.StatusCode = StatusCodes.Status404NotFound;
                return;
            }

            await next();
        });

    protected override IDictionary<string, string?> Configuration(HostContext context)
    {
        var compute = context.Settings.Compute;
        var (pageserver, safekeeper, postgres) = compute.NetworkingOnly
            ? (Stub(context), Stub(context), Stub(context))
            : (compute.PageserverPath, compute.SafekeeperPath, compute.PostgresPath);

        var config = new Dictionary<string, string?>
        {
            ["ComputeNode:DataRoot"] = context.RoleDataDir(Name),
            ["ComputeNode:PageserverExecutablePath"] = pageserver,
            ["ComputeNode:SafekeeperExecutablePath"] = safekeeper,
            ["ComputeNode:PostgresExecutablePath"] = postgres,
        };
        RoleNetwork.Add(config, "ComputeNode", context, HostContext.EndpointFor(compute.AdvertisedEndpoint, compute.Port));
        return config;
    }

    private static string Stub(HostContext context)
    {
        if (OperatingSystem.IsWindows())
        {
            throw new ComponentFaultException("Networking-only compute stubs are not supported on Windows.");
        }

        var path = context.Paths.NeonStub;
        if (!File.Exists(path))
        {
            File.WriteAllText(path, "#!/bin/sh\n# Stand-in for Neon binaries in networking-only mode (issue #133).\nexec sleep 2147483647\n");
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }

        return path;
    }
}

/// <summary>
/// The S3-compatible blob gateway role (issue #135). Storage nodes come from the on-chain
/// registry; writes succeed at <see cref="Settings.BlobGatewaySettings.WriteQuorum"/> copies and
/// missing copies up to <see cref="Settings.BlobGatewaySettings.MinCopies"/> are repaired promptly.
/// </summary>
public sealed class BlobGatewayRoleComponent : WebAppComponent
{
    public override string Name => "blobgateway";

    protected override bool UsesRoleDataDir => true;

    protected override bool BindsOwnPort => true;

    protected override string? SkipReason(HostContext context)
    {
        var gateway = context.Settings.BlobGateway;
        if (!gateway.Enabled)
        {
            return "Blob Gateway service is turned off";
        }

        if (string.IsNullOrWhiteSpace(gateway.AccessKeyId) || !context.Secrets.HasBlobGatewaySecret)
        {
            return "Set an access key ID and secret under Node Services to start the Blob Gateway";
        }

        return base.SkipReason(context);
    }

    protected override WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder> configure, HostContext context) =>
        BlobGatewayApp.Create(options, configure);

    protected override int Port(HostContext context) => context.Settings.BlobGateway.Port;

    protected override (byte Service, long Capacity)? StakedService(HostContext context) => (8, 0);

    protected override IDictionary<string, string?> Configuration(HostContext context)
    {
        var gateway = context.Settings.BlobGateway;
        var network = context.Network!;
        return new Dictionary<string, string?>
        {
            ["Gateway:ListenPort"] = gateway.Port.ToString(),
            ["Gateway:Region"] = gateway.Region,
            ["Gateway:MetadataDbPath"] = Path.Combine(context.RoleDataDir(Name), "gateway-metadata.db"),
            ["Gateway:WriteQuorumCount"] = gateway.WriteQuorum.ToString(),
            ["Gateway:MinCopiesPerObject"] = gateway.MinCopies.ToString(),
            ["Gateway:ProbeIntervalSeconds"] = "10",
            ["Gateway:ReplicationCheckIntervalSeconds"] = "60",
            ["Gateway:NodeSource"] = "Registry",
            ["Gateway:BlockchainGatewayUrl"] = network.GatewayUrl,
            // Same cadence as peer discovery: both read the same registry, so one knob for both.
            ["Gateway:RegistryRefreshSeconds"] = context.Settings.Discovery.IntervalSeconds.ToString(),
            // Storage RPCs are signed with this host's node key (issue #140).
            ["Gateway:NodeIdentityKeyPath"] = context.Paths.NodeKey,
            ["Gateway:ChainId"] = network.ChainId.ToString(),
            ["Gateway:NodeRegistryAddress"] = network.NodeRegistryAddress,
            // Whether cleartext storage nodes are still acceptable is network policy, read from the
            // chain's protocol activations through the Blockchain Gateway (issue #145), not a local switch.
            ["Gateway:Credentials:0:AccessKeyId"] = gateway.AccessKeyId,
            ["Gateway:Credentials:0:SecretAccessKey"] = context.Secrets.BlobGatewaySecret,
            ["Gateway:Credentials:0:TenantId"] = "dev",
        };
    }
}
