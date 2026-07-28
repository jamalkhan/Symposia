namespace Symposia.Database.ComputeNode.Supervision;

/// <summary>Abstraction over spawning a real OS process, so supervision logic can be unit-tested without real binaries.</summary>
public interface IProcessLauncher
{
    /// <summary>
    /// Starts a child process. <paramref name="environment"/> is preferred over command-line
    /// arguments for secrets (e.g. blob bucket credentials): argv is visible to other local
    /// users via /proc or `ps`, whereas the child's own environment block is not.
    /// </summary>
    ILaunchedProcess Start(string executablePath, string arguments, IReadOnlyDictionary<string, string>? environment = null);
}

/// <summary>A single spawned child process handle.</summary>
public interface ILaunchedProcess : IDisposable
{
    bool HasExited { get; }

    int ExitCode { get; }

    event EventHandler Exited;

    void Kill();
}
