using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace Symposia.NodeHost.Settings;

/// <summary>
/// Layers shipped defaults, the operator's settings.json, and SYMPOSIA__-prefixed
/// environment variables (which win and are reported as locked, FR-E6).
/// </summary>
public sealed class SettingsStore
{
    public const string EnvironmentPrefix = "SYMPOSIA__";

    private readonly HostPaths _paths;
    private readonly int _portOffset;
    private readonly IConfiguration _environment;
    private readonly object _gate = new();
    private HostSettings _file;

    public SettingsStore(HostPaths paths, int portOffset, IConfiguration? environment = null)
    {
        _paths = paths;
        _portOffset = portOffset;
        _environment = environment ?? new ConfigurationBuilder().AddEnvironmentVariables(EnvironmentPrefix).Build();
        _file = SecureFile.Read<HostSettings>(paths.Settings) ?? HostSettings.Defaults(portOffset);
        LockedKeys = _environment.AsEnumerable()
            .Where(kv => kv.Value is not null)
            .Select(kv => kv.Key)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Settings keys (e.g. <c>Storage:Enabled</c>) fixed by environment variables.</summary>
    public IReadOnlyList<string> LockedKeys { get; }

    /// <summary>The settings in force: the operator's file with environment overrides applied.</summary>
    public HostSettings Effective
    {
        get
        {
            lock (_gate)
            {
                var copy = Clone(_file);
                _environment.Bind(copy);
                return copy;
            }
        }
    }

    public void Save(HostSettings settings)
    {
        lock (_gate)
        {
            _file = Clone(settings);
            SecureFile.Write(_paths.Settings, _file, ownerOnly: false);
        }
    }

    private static HostSettings Clone(HostSettings settings) =>
        JsonSerializer.Deserialize<HostSettings>(JsonSerializer.Serialize(settings, SecureFile.Json), SecureFile.Json)!;
}
