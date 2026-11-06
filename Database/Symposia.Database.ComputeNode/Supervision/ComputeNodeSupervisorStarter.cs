namespace Symposia.Database.ComputeNode.Supervision;

/// <summary>Starts the pageserver/safekeeper supervisor when the host starts (issue #133).</summary>
public sealed class ComputeNodeSupervisorStarter(ComputeNodeSupervisor supervisor) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        supervisor.Start();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
