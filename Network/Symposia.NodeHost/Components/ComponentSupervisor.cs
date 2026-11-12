using Microsoft.Extensions.Logging;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Components;

/// <summary>
/// Starts components in dependency order (chain → gateway → network → roles),
/// stops them in reverse, and restarts only what a settings change affects
/// (issue #133 FR-A3, FR-D2, FR-E3).
/// </summary>
public sealed class ComponentSupervisor(HostContext context, SettingsStore settings)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    public ChainComponent Chain { get; } = new();

    public GatewayComponent Gateway { get; } = new();

    public NetworkComponent Network { get; } = new();

    public StorageRoleComponent Storage { get; } = new();

    public ComputeRoleComponent Compute { get; } = new();

    public BlobGatewayRoleComponent BlobGateway { get; } = new();

    public VerifierRoleComponent Verifier { get; } = new();

    public HostContext Context => context;

    public IReadOnlyList<HostComponent> All => [Chain, Gateway, Network, Storage, Compute, BlobGateway, Verifier];

    public async Task StartAllAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            context.Settings = settings.Effective;
            await StartAsync(All, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task StopAllAsync()
    {
        await _gate.WaitAsync();
        try
        {
            await StopAsync(All);
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Saves new settings and applies them, restarting only affected components.
    /// Read-previous, save, and apply happen under one lock so concurrent saves
    /// can't skip a restart.
    /// </summary>
    /// <returns>The components that were restarted.</returns>
    public async Task<IReadOnlyList<HostComponent>> SaveAndApplyAsync(HostSettings updated, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var previous = settings.Effective;
            settings.Save(updated);
            var next = settings.Effective;
            var affected = Affected(previous, next);
            context.Settings = next;
            await StopAsync(affected);
            await StartAsync(affected, ct);
            return affected;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// Keeps retrying when the network can't be reached: a Join or remote-chain host that starts
    /// before its gateway or RPC is up (both machines rebooting, say) would otherwise sit with every
    /// role "waiting for the network" until someone saves settings. Backs off 5 s → 60 s; runs until shutdown.
    /// </summary>
    public async Task RecoverNetworkAsync(CancellationToken ct)
    {
        var delay = TimeSpan.FromSeconds(5);
        while (!ct.IsCancellationRequested)
        {
            await Task.Delay(delay, ct);
            var mode = context.Settings.Network.Mode;
            if (Chain.Status != ComponentStatus.Faulted || mode is not (NetworkMode.Join or NetworkMode.RemoteRpc))
            {
                delay = TimeSpan.FromSeconds(5);
                continue;
            }

            context.Logger.LogInformation("Network unreachable ({Reason}); retrying.", Chain.Reason);
            await RestartAsync(Chain.Name, ct);
            delay = Chain.Status == ComponentStatus.Faulted ? TimeSpan.FromSeconds(Math.Min(60, delay.TotalSeconds * 2)) : TimeSpan.FromSeconds(5);
        }
    }

    /// <summary>Restarts the blob gateway so a replaced secret takes effect (no settings change involved).</summary>
    public Task RestartBlobGatewayAsync(CancellationToken ct) => RestartAsync(BlobGateway.Name, ct);

    public async Task RestartAsync(string name, CancellationToken ct)
    {
        var component = All.FirstOrDefault(c => c.Name == name)
            ?? throw new ArgumentException($"Unknown component '{name}'.");

        await _gate.WaitAsync(ct);
        try
        {
            // Restarting the chain changes what every downstream component points at.
            IReadOnlyList<HostComponent> affected = component == Chain ? All : [component];
            context.Settings = settings.Effective;
            await StopAsync(affected);
            await StartAsync(affected, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal IReadOnlyList<HostComponent> Affected(HostSettings previous, HostSettings next)
    {
        if (Changed(previous.Network, next.Network))
        {
            return All;
        }

        var affected = new List<HostComponent>();
        var storageChanged = Changed(previous.Storage, next.Storage);
        var computeChanged = Changed(previous.Compute, next.Compute);
        var secret = context.Secrets.HasBlobGatewaySecret;
        if (HostContext.PrimaryEndpointFor(previous, secret) != HostContext.PrimaryEndpointFor(next, secret) || Changed(previous.Discovery, next.Discovery))
        {
            // Only these change what the network component registers or how it discovers.
            affected.Add(Network);
        }

        // The primary role serves the handshake, health and service directory (#228): when it moves, both the old and
        // the new primary restart, so the paths move with it.
        var (was, now) = (HostContext.PrimaryRoleFor(previous, secret), HostContext.PrimaryRoleFor(next, secret));
        if (was != now)
        {
            foreach (var role in new[] { was, now })
            {
                if (RoleComponent(role) is { } component && !affected.Contains(component))
                {
                    affected.Add(component);
                }
            }
        }

        if (storageChanged)
        {
            affected.Add(Storage);
        }

        if (computeChanged)
        {
            affected.Add(Compute);
        }

        // A running gateway also takes its registry refresh interval from Discovery.
        if (Changed(previous.BlobGateway, next.BlobGateway) || (next.BlobGateway.Enabled && Changed(previous.Discovery, next.Discovery)))
        {
            affected.Add(BlobGateway);
        }

        // The primary role serves the signed service directory, which lists the gateway's URL (#225).
        if (Changed(previous.BlobGateway, next.BlobGateway) && !affected.Contains(Storage) && !affected.Contains(Compute)
            && (next.Storage.Enabled ? Storage : next.Compute.Enabled ? (HostComponent)Compute : null) is { } primary)
        {
            affected.Add(primary);
        }

        if (Changed(previous.Verifier, next.Verifier))
        {
            affected.Add(Verifier);
        }

        return [.. affected.Distinct()];
    }

    private HostComponent? RoleComponent(PrimaryRole role) => role switch
    {
        PrimaryRole.Storage => Storage,
        PrimaryRole.Compute => Compute,
        PrimaryRole.BlobGateway => BlobGateway,
        _ => null,
    };

    private static bool Changed<T>(T a, T b) =>
        System.Text.Json.JsonSerializer.Serialize(a, SecureFile.Json) != System.Text.Json.JsonSerializer.Serialize(b, SecureFile.Json);

    private async Task StartAsync(IReadOnlyList<HostComponent> components, CancellationToken ct)
    {
        foreach (var component in All.Where(components.Contains))
        {
            if (component == Chain)
            {
                context.Network = null;
            }

            await component.StartAsync(context, ct);
        }
    }

    private async Task StopAsync(IReadOnlyList<HostComponent> components)
    {
        foreach (var component in All.Reverse().Where(components.Contains))
        {
            await component.StopAsync();
        }
    }
}
