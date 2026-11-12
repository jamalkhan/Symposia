using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;
using Symposia.Blockchain.Gateway.Chain;

namespace Symposia.Blockchain.Gateway.Tests;

/// <summary>
/// A fixture's anvil, started on a port anvil picks itself (#186). Picking a free port first and handing it to
/// anvil left a gap in which another listener could take it; the readiness probe then reached that listener and
/// the deploy hung for minutes. anvil prints "Listening on" only once it has bound, so that line is also the
/// readiness signal.
/// </summary>
public sealed partial class AnvilProcess : IAsyncDisposable
{
    private static readonly TimeSpan StartTimeout = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan ExitTimeout = TimeSpan.FromSeconds(10);

    /// <summary>Where anvil and forge are installed (~/.foundry/bin).</summary>
    public static string FoundryBin { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".foundry", "bin");

    private readonly Process _process;

    private AnvilProcess(Process process, int port) => (_process, Port) = (process, port);

    public int Port { get; }

    public string RpcUrl => $"http://127.0.0.1:{Port}";

    public static Task<AnvilProcess> StartAsync() => StartAsync(Path.Combine(FoundryBin, "anvil"), StartTimeout);

    internal static async Task<AnvilProcess> StartAsync(string anvilPath, TimeSpan startTimeout)
    {
        var process = new Process
        {
            StartInfo = new ProcessStartInfo(anvilPath, AnvilArgs.Fixture())
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };
        var output = new StringBuilder();
        var listening = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        // Drained for the process's life: without --silent anvil logs every RPC, and a full pipe would stall it.
        void OnLine(object _, DataReceivedEventArgs e)
        {
            if (e.Data is null || listening.Task.IsCompleted)
            {
                return;
            }

            lock (output)
            {
                output.AppendLine(e.Data);
            }

            if (ParsePort(e.Data) is { } port)
            {
                listening.TrySetResult(port);
            }
        }

        process.OutputDataReceived += OnLine;
        process.ErrorDataReceived += OnLine;
        process.Exited += (_, _) =>
        {
            // Flushes the async readers, so the message carries anvil's last lines.
            process.WaitForExit();
            listening.TrySetException(new InvalidOperationException($"anvil exited before listening:\n{Captured()}"));
        };

        try
        {
            try
            {
                process.Start();
            }
            catch (System.ComponentModel.Win32Exception ex)
            {
                throw new InvalidOperationException($"Failed to start anvil at {anvilPath}.", ex);
            }

            process.BeginOutputReadLine();
            process.BeginErrorReadLine();
            return new AnvilProcess(process, await listening.Task.WaitAsync(startTimeout));
        }
        catch (Exception ex)
        {
            await StopAsync(process);
            throw ex is TimeoutException
                ? new InvalidOperationException($"anvil did not report a port within {startTimeout.TotalSeconds} s:\n{Captured()}")
                : ex;
        }

        string Captured()
        {
            lock (output)
            {
                return output.ToString();
            }
        }
    }

    /// <summary>The port from anvil's "Listening on 127.0.0.1:NNNN" line, or null for any other line.</summary>
    internal static int? ParsePort(string line) =>
        ListeningLine().Match(line) is { Success: true } m ? int.Parse(m.Groups[1].Value) : null;

    [GeneratedRegex(@"^Listening on [^\s]+:(\d+)\s*$")]
    private static partial Regex ListeningLine();

    public ValueTask DisposeAsync() => new(StopAsync(_process));

    /// <summary>Kills the process tree and waits, bounded, so a kill that doesn't take can't hang teardown.</summary>
    private static async Task StopAsync(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
            using var limit = new CancellationTokenSource(ExitTimeout);
            await process.WaitForExitAsync(limit.Token);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException)
        {
            // Never started, already exited, or didn't exit in time; nothing more to do in a test teardown.
        }

        process.Dispose();
    }
}
