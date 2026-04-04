namespace InboxWeb;

internal static class PathResolution
{
    public static bool IsRunningInContainer =>
        string.Equals(Environment.GetEnvironmentVariable("DOTNET_RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase)
        || string.Equals(Environment.GetEnvironmentVariable("RUNNING_IN_CONTAINER"), "true", StringComparison.OrdinalIgnoreCase);

    public static string? ResolveOptionalPath(string? configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return null;
        }

        return ResolvePath(configuredPath);
    }

    public static string ResolvePath(string configuredPath)
    {
        if (Path.IsPathRooted(configuredPath))
        {
            return configuredPath;
        }

        var candidates = new[]
        {
            Environment.CurrentDirectory,
            AppContext.BaseDirectory,
            Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", ".."))
        }
        .Distinct(StringComparer.OrdinalIgnoreCase);

        foreach (var basePath in candidates)
        {
            var candidate = Path.GetFullPath(configuredPath, basePath);
            if (File.Exists(candidate) || Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        return Path.GetFullPath(configuredPath, Environment.CurrentDirectory);
    }
}
