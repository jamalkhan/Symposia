using Microsoft.Extensions.Logging.Abstractions;
using Symposia.Database.ComputeNode;
using Symposia.Database.ComputeNode.Supervision;

namespace Symposia.Database.ComputeNode.Tests.Supervision;

public class ManagedProcessTests
{
    private static ComputeNodeOptions FastRetryOptions(int maxRestartAttempts = 3) => new()
    {
        RestartBackoffBaseSeconds = 0,
        MaxRestartAttempts = maxRestartAttempts,
        CrashLoopWindowSeconds = 300,
    };

    [Fact]
    public void Start_LaunchesProcessAndReportsRunning()
    {
        var launcher = new FakeProcessLauncher();
        var process = new ManagedProcess("pageserver", "pageserver", "", launcher, FastRetryOptions(), NullLogger.Instance);

        process.Start();

        Assert.Equal(ProcessState.Running, process.State);
        Assert.Single(launcher.Launched);
    }

    [Fact]
    public async Task OnCrash_RestartsProcessWithinBackoff()
    {
        var launcher = new FakeProcessLauncher();
        var process = new ManagedProcess("pageserver", "pageserver", "", launcher, FastRetryOptions(), NullLogger.Instance);
        process.Start();

        launcher.Launched[0].SimulateCrash();

        // Backoff base is 0s, so the restart should land almost immediately.
        await WaitUntilAsync(() => process.State == ProcessState.Running, TimeSpan.FromSeconds(2));

        Assert.Equal(2, launcher.Launched.Count);
        Assert.Equal(1, process.RestartAttemptCount);
    }

    [Fact]
    public async Task RepeatedCrashesBeyondThreshold_MarksUnhealthyInsteadOfRestarting()
    {
        var launcher = new FakeProcessLauncher();
        var process = new ManagedProcess("pageserver", "pageserver", "", launcher, FastRetryOptions(maxRestartAttempts: 2), NullLogger.Instance);
        process.Start();

        for (var i = 0; i < 3; i++)
        {
            var current = launcher.Launched[^1];
            current.SimulateCrash();
            await WaitUntilAsync(
                () => process.State is ProcessState.Running or ProcessState.Unhealthy,
                TimeSpan.FromSeconds(2));
        }

        Assert.Equal(ProcessState.Unhealthy, process.State);
    }

    [Fact]
    public void Stop_DoesNotTriggerRestart()
    {
        var launcher = new FakeProcessLauncher();
        var process = new ManagedProcess("pageserver", "pageserver", "", launcher, FastRetryOptions(), NullLogger.Instance);
        process.Start();

        process.Stop();
        launcher.Launched[0].SimulateCrash();

        Assert.Equal(ProcessState.Stopped, process.State);
        Assert.Single(launcher.Launched);
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (!condition() && DateTime.UtcNow < deadline)
            await Task.Delay(10);

        Assert.True(condition(), "Condition was not met within the timeout.");
    }
}
