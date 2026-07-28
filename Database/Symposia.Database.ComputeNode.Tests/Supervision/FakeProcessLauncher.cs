using Symposia.Database.ComputeNode.Supervision;

namespace Symposia.Database.ComputeNode.Tests.Supervision;

/// <summary>Test double letting a test start a process and then simulate a crash on demand.</summary>
public sealed class FakeProcessLauncher : IProcessLauncher
{
    public List<FakeLaunchedProcess> Launched { get; } = [];

    public ILaunchedProcess Start(string executablePath, string arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var process = new FakeLaunchedProcess(executablePath, arguments, environment);
        Launched.Add(process);
        return process;
    }
}

public sealed class FakeLaunchedProcess : ILaunchedProcess
{
    public string ExecutablePath { get; }

    public string Arguments { get; }

    public IReadOnlyDictionary<string, string>? Environment { get; }

    public bool HasExited { get; private set; }

    public int ExitCode { get; private set; }

    public event EventHandler? Exited;

    public FakeLaunchedProcess(string executablePath, string arguments, IReadOnlyDictionary<string, string>? environment)
    {
        ExecutablePath = executablePath;
        Arguments = arguments;
        Environment = environment;
    }

    public void SimulateCrash(int exitCode = 1)
    {
        HasExited = true;
        ExitCode = exitCode;
        Exited?.Invoke(this, EventArgs.Empty);
    }

    public void Kill() => HasExited = true;

    public void Dispose() { }
}
