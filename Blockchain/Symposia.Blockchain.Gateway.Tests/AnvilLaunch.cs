namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>Shared anvil arguments for the test fixtures.</summary>
internal static class AnvilLaunch
{
    /// <summary>
    /// --prune-history: fixtures are hard-killed, and anvil only removes its ~/.foundry/anvil/tmp history
    /// snapshots on a clean exit, so never write any (#210).
    /// </summary>
    internal static string Arguments(int port) => $"--port {port} --prune-history 64 --silent";
}
