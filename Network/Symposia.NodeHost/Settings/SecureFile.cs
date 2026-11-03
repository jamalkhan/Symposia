using System.Text;
using System.Text.Json;

namespace Symposia.NodeHost.Settings;

/// <summary>Atomic JSON writes, optionally owner-only from creation (no permission window).</summary>
internal static class SecureFile
{
    public static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() },
    };

    /// <summary>Null when the file doesn't exist; throws <see cref="CorruptFileException"/> when it can't be parsed.</summary>
    public static T? Read<T>(string path) where T : class
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json)
                ?? throw new CorruptFileException(path, "it is empty");
        }
        catch (JsonException ex)
        {
            throw new CorruptFileException(path, ex.Message);
        }
    }

    public static void Write<T>(string path, T value, bool ownerOnly)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        var options = new FileStreamOptions { Mode = FileMode.Create, Access = FileAccess.Write };
        if (ownerOnly && !OperatingSystem.IsWindows())
        {
            options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        }

        File.Delete(temp);
        using (var stream = new FileStream(temp, options))
        {
            stream.Write(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(value, Json)));
            // Durable before the rename, so a power cut never leaves a truncated file in place.
            stream.Flush(flushToDisk: true);
        }

        File.Move(temp, path, overwrite: true);
    }
}

/// <summary>A data-dir file exists but can't be parsed; the operator must fix or remove it.</summary>
public sealed class CorruptFileException(string path, string detail)
    : Exception($"'{path}' is corrupt ({detail}). Restore it from backup, or delete it to reset that part of the node's configuration.")
{
    public string Path { get; } = path;
}
