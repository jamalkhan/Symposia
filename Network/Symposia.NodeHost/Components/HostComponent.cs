namespace Symposia.NodeHost.Components;

public enum ComponentStatus
{
    Stopped,
    Starting,
    Running,
    Faulted,
    Disabled,
}

/// <summary>
/// One independently startable piece of the Node Host (issue #133). A failure
/// in one component is recorded as <see cref="ComponentStatus.Faulted"/> with a
/// reason and never takes down the host or other components (FR-A4).
/// </summary>
public abstract class HostComponent
{
    private readonly SemaphoreSlim _lifecycle = new(1, 1);

    public abstract string Name { get; }

    public ComponentStatus Status { get; private set; } = ComponentStatus.Stopped;

    public string? Reason { get; private set; }

    public async Task StartAsync(HostContext context, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            if (Status is ComponentStatus.Running or ComponentStatus.Starting)
            {
                return;
            }

            var skip = SkipReason(context);
            if (skip is not null)
            {
                Set(ComponentStatus.Disabled, skip);
                return;
            }

            Set(ComponentStatus.Starting, null);
            try
            {
                await OnStartAsync(context, ct);
                Set(ComponentStatus.Running, null);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                Set(ComponentStatus.Faulted, ex is ComponentFaultException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
                await SafeStopAsync();
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    public async Task StopAsync()
    {
        await _lifecycle.WaitAsync();
        try
        {
            await SafeStopAsync();
            if (Status != ComponentStatus.Faulted)
            {
                Set(ComponentStatus.Stopped, null);
            }
        }
        finally
        {
            _lifecycle.Release();
        }
    }

    /// <summary>Non-null when this component should not run under the current settings.</summary>
    protected virtual string? SkipReason(HostContext context) => null;

    protected abstract Task OnStartAsync(HostContext context, CancellationToken ct);

    protected abstract Task OnStopAsync();

    private async Task SafeStopAsync()
    {
        try
        {
            await OnStopAsync();
        }
        catch
        {
            // Stopping is best effort; the status already says why it went down.
        }
    }

    private void Set(ComponentStatus status, string? reason)
    {
        Status = status;
        Reason = reason;
    }
}

/// <summary>A start failure whose message is shown to the operator as-is.</summary>
public sealed class ComponentFaultException(string message) : Exception(message);
