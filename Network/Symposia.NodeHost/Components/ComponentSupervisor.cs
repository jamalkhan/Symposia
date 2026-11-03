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

    public HostContext Context => context;

    public IReadOnlyList<HostComponent> All => [Chain, Gateway, Network, Storage, Compute];

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

    /// <summary>Applies new effective settings, restarting only affected components.</summary>
    public async Task ApplyAsync(HostSettings previous, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            var next = settings.Effective;
            var affected = Affected(previous, next);
            context.Settings = next;
            await StopAsync(affected);
            await StartAsync(affected, ct);
        }
        finally
        {
            _gate.Release();
        }
    }

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
        if (storageChanged || computeChanged || Changed(previous.Discovery, next.Discovery))
        {
            // The primary endpoint, or discovery settings, may have moved.
            affected.Add(Network);
        }

        if (storageChanged)
        {
            affected.Add(Storage);
        }

        if (computeChanged)
        {
            affected.Add(Compute);
        }

        return affected;
    }

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
