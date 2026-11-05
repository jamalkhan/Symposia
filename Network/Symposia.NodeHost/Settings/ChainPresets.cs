using System.Text.Json;

namespace Symposia.NodeHost.Settings;

/// <param name="ProtocolActivationsAddress">The network's ProtocolActivations contract (issue #145); empty = none yet.</param>
public sealed record ChainPreset(string Description, string RpcUrl, ulong ChainId, string NodeRegistryAddress, string EpochRootRegistryAddress,
    string ProtocolActivationsAddress = "")
{
    public bool IsDeployed =>
        !string.IsNullOrWhiteSpace(RpcUrl) && ChainId != 0 &&
        !string.IsNullOrWhiteSpace(NodeRegistryAddress) && !string.IsNullOrWhiteSpace(EpochRootRegistryAddress);
}

/// <summary>Named remote chains shipped as data, not code (FR-B3).</summary>
public static class ChainPresets
{
    public static IReadOnlyDictionary<string, ChainPreset> All { get; } = Load();

    /// <summary>The preset named in <paramref name="network"/>, with any explicit fields overriding it.</summary>
    public static ChainPreset Resolve(NetworkSettings network)
    {
        var preset = All.GetValueOrDefault(network.Preset) ?? new ChainPreset("custom", "", 0, "", "");
        return new ChainPreset(
            preset.Description,
            Pick(network.RpcUrl, preset.RpcUrl),
            network.ChainId != 0 ? network.ChainId : preset.ChainId,
            Pick(network.NodeRegistryAddress, preset.NodeRegistryAddress),
            Pick(network.EpochRootRegistryAddress, preset.EpochRootRegistryAddress),
            preset.ProtocolActivationsAddress);
    }

    private static string Pick(string over, string preset) => string.IsNullOrWhiteSpace(over) ? preset : over;

    private static Dictionary<string, ChainPreset> Load()
    {
        using var stream = typeof(ChainPresets).Assembly.GetManifestResourceStream("Symposia.NodeHost.Settings.presets.json")
            ?? throw new InvalidOperationException("presets.json resource missing.");
        return JsonSerializer.Deserialize<Dictionary<string, ChainPreset>>(stream, SecureFile.Json)!;
    }
}
