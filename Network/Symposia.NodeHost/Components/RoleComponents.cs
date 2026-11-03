using Symposia.BlobStorage.StorageNode;
using Symposia.Database.ComputeNode;

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

    protected override bool BindsOwnPort => true;

    protected override string? SkipReason(HostContext context) =>
        !context.Settings.Storage.Enabled ? "Storage role is turned off" : base.SkipReason(context);

    protected override WebApplication Build(WebApplicationOptions options, Action<WebApplicationBuilder> configure, HostContext context) =>
        StorageNodeApp.Create(options, configure);

    protected override int Port(HostContext context) => context.Settings.Storage.Port;

    protected override IDictionary<string, string?> Configuration(HostContext context)
    {
        var storage = context.Settings.Storage;
        var dir = context.RoleDataDir(Name);
        var config = new Dictionary<string, string?>
        {
            ["StorageNode:ListenPort"] = storage.Port.ToString(),
            ["StorageNode:StorageRoot"] = Path.Combine(dir, "blobs"),
            ["StorageNode:ManifestDbPath"] = Path.Combine(dir, "manifest.db"),
            ["StorageNode:MaxCapacityBytes"] = storage.MaxCapacityBytes.ToString(),
        };
        RoleNetwork.Add(config, "StorageNode", context, HostContext.EndpointFor(storage.AdvertisedEndpoint, storage.Port));
        return config;
    }
}

/// <summary>The compute node role (FR-D1), optionally with stub Neon binaries for networking-only testing.</summary>
public sealed class ComputeRoleComponent : WebAppComponent
{
    public override string Name => "compute";

    protected override string? SkipReason(HostContext context) =>
        !context.Settings.Compute.Enabled ? "Compute role is turned off" : base.SkipReason(context);

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
            if (remote is not null && !System.Net.IPAddress.IsLoopback(remote) && http.Request.Path != "/v1/peer/handshake")
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
