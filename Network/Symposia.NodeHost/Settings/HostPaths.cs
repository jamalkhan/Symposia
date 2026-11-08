namespace Symposia.NodeHost.Settings;

/// <summary>Files under the host data directory (FR-E5).</summary>
public sealed record HostPaths(string DataDir)
{
    public string Settings => Path.Combine(DataDir, "settings.json");

    public string Secrets => Path.Combine(DataDir, "secrets.json");

    public string NodeKey => Path.Combine(DataDir, "node.key");

    public string Deployment => Path.Combine(DataDir, "network.json");

    public string AnvilState => Path.Combine(DataDir, "anvil-state.json");

    /// <summary>One-shot request to redeploy the LocalDev rewards contracts on the next chain start (#181).</summary>
    public string ResetRewardsFlag => Path.Combine(DataDir, "reset-rewards.flag");

    public string NeonStub => Path.Combine(DataDir, "neon-stub");

    public string Component(string name) => Path.Combine(DataDir, name);

    public string Logs(string service) => Path.Combine(DataDir, "logs", service);

    public static string DefaultDataDir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".symposia", "node");
}
