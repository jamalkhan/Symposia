using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

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
    private ILogger _logger = NullLogger.Instance;

    public abstract string Name { get; }

    public ComponentStatus Status { get; private set; } = ComponentStatus.Stopped;

    public string? Reason { get; private set; }

    public async Task StartAsync(HostContext context, CancellationToken ct)
    {
        await _lifecycle.WaitAsync(ct);
        try
        {
            _logger = context.CreateLogger(Name, $"Symposia.NodeHost.Components.{GetType().Name}");
            if (Status is ComponentStatus.Running or ComponentStatus.Starting)
            {
                _logger.LogDebug("Start requested but component is already {Status}", Status);
                return;
            }

            var skip = SkipReason(context);
            if (skip is not null)
            {
                Set(ComponentStatus.Disabled, skip);
                _logger.LogInformation("Not starting: {Reason}", skip);
                return;
            }

            Set(ComponentStatus.Starting, null);
            _logger.LogInformation("Starting");
            var started = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                await OnStartAsync(context, ct);
                Set(ComponentStatus.Running, null);
                _logger.LogInformation("Running (started in {ElapsedMs} ms)", started.ElapsedMilliseconds);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                // Host shutting down mid-start: release anything half-started (e.g. anvil).
                _logger.LogWarning("Start cancelled because the host is shutting down");
                await SafeStopAsync();
                Set(ComponentStatus.Stopped, "Start cancelled");
                throw;
            }
            catch (Exception ex)
            {
                Set(ComponentStatus.Faulted, ex is ComponentFaultException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}");
                if (ex is ComponentFaultException)
                {
                    _logger.LogError("Faulted: {Reason}", ex.Message);
                }
                else
                {
                    _logger.LogError(ex, "Faulted while starting");
                }

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
            if (Status is ComponentStatus.Running or ComponentStatus.Starting)
            {
                _logger.LogInformation("Stopping");
            }

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
        catch (Exception ex)
        {
            // Stopping is best effort; the status already says why it went down.
            _logger.LogWarning(ex, "Error while stopping (ignored)");
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
