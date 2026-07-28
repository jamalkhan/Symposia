using System.Diagnostics;

namespace Symposia.Database.ComputeNode.Supervision;

/// <summary>Real <see cref="IProcessLauncher"/> backed by <see cref="Process"/>.</summary>
public sealed class OsProcessLauncher : IProcessLauncher
{
    public ILaunchedProcess Start(string executablePath, string arguments, IReadOnlyDictionary<string, string>? environment = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            Arguments = arguments,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };

        if (environment is not null)
        {
            foreach (var (key, value) in environment)
                startInfo.Environment[key] = value;
        }

        var process = new Process
        {
            StartInfo = startInfo,
            EnableRaisingEvents = true,
        };

        process.Start();
        return new OsLaunchedProcess(process);
    }

    private sealed class OsLaunchedProcess : ILaunchedProcess
    {
        private readonly Process _process;

        public OsLaunchedProcess(Process process)
        {
            _process = process;
            _process.Exited += (_, e) => Exited?.Invoke(this, e);
        }

        public bool HasExited => _process.HasExited;

        public int ExitCode => _process.ExitCode;

        public event EventHandler? Exited;

        public void Kill()
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }

        public void Dispose() => _process.Dispose();
    }
}
