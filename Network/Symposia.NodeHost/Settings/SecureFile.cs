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

    public static T? Read<T>(string path) where T : class =>
        File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path), Json) : null;

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
        using (var writer = new StreamWriter(temp, Encoding.UTF8, options))
        {
            writer.Write(JsonSerializer.Serialize(value, Json));
        }

        File.Move(temp, path, overwrite: true);
    }
}
