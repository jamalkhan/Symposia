using Microsoft.Extensions.Options;
using Symposia.Network.Node;
using Symposia.NodeHost.Components;
using Symposia.NodeHost.Diagnostics;
using Symposia.NodeHost.Settings;
using Symposia.NodeHost.Ui;

// Symposia Node Host (issue #133): one process running the gateway, the
// config/diagnostics UI, and the storage/compute roles.
//
//   dotnet run --project Network/Symposia.NodeHost [-- --data-dir DIR] [--port-offset N] [--ui-port P]

var cli = HostCommandLine.Parse(args);
var paths = new HostPaths(cli.DataDir);
Directory.CreateDirectory(paths.DataDir);

// One identity for every role (FR-A2). Loading it first fails fast, before
// anything starts, if an initialized node has lost its key (#129 FR-A3).
var identity = new NodeIdentity(Options.Create(new NodeNetworkOptions { NodeIdentityKeyPath = paths.NodeKey }));
try
{
    identity.EnsureLoadedOrGenerated();
}
catch (NodeIdentityException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

var logs = new LogBuffer();
var settings = new SettingsStore(paths, cli.PortOffset);
var secrets = new SecretStore(paths);
var supervisor = new ComponentSupervisor(new HostContext(paths, secrets, logs), settings);
var uiPort = cli.UiPort ?? 5170 + cli.PortOffset;

var ui = UiApp.Create(uiPort, supervisor, settings, secrets, identity.NodeId);
await ui.StartAsync();

Console.WriteLine($"""

  Symposia Node Host
    node address : {identity.NodeId}
    data dir     : {paths.DataDir}
    config UI    : http://localhost:{uiPort}{(secrets.HasPassword ? "" : "   (first run: open it to set an admin password)")}

""");

var startup = Task.Run(() => supervisor.StartAllAsync(ui.Lifetime.ApplicationStopping));
await ui.WaitForShutdownAsync();

try
{
    await startup;
}
catch (OperationCanceledException)
{
}

await supervisor.StopAllAsync();
await ui.StopAsync();
return 0;

internal sealed record HostCommandLine(string DataDir, int PortOffset, int? UiPort)
{
    public static HostCommandLine Parse(string[] args)
    {
        string dataDir = HostPaths.DefaultDataDir;
        int offset = 0;
        int? uiPort = null;
        for (var i = 0; i < args.Length; i++)
        {
            string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{args[i]} needs a value.");
            switch (args[i])
            {
                case "--data-dir": dataDir = Path.GetFullPath(Next()); break;
                case "--port-offset": offset = int.Parse(Next()); break;
                case "--ui-port": uiPort = int.Parse(Next()); break;
                default: throw new ArgumentException($"Unknown argument '{args[i]}'. Use --data-dir, --port-offset, --ui-port.");
            }
        }

        return new HostCommandLine(dataDir, offset, uiPort);
    }
}
