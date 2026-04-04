using Microsoft.Extensions.Hosting;

namespace NativeSmtpReceiver;

public sealed class BasemailMailboxRegistrySyncWorker : BackgroundService
{
    private readonly BasemailNodeOptions _options;
    private readonly BasemailMailboxRegistryService _mailboxRegistryService;
    private readonly ILogger<BasemailMailboxRegistrySyncWorker> _logger;

    public BasemailMailboxRegistrySyncWorker(
        BasemailNodeOptions options,
        BasemailMailboxRegistryService mailboxRegistryService,
        ILogger<BasemailMailboxRegistrySyncWorker> logger)
    {
        _options = options;
        _mailboxRegistryService = mailboxRegistryService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.NetworkEnabled || _options.Peers.Count == 0)
        {
            return;
        }

        var interval = TimeSpan.FromSeconds(Math.Max(1, _options.RegistrySyncSeconds));

        try
        {
            await _mailboxRegistryService.NotifyPeersOfCurrentVersionAsync(stoppingToken);
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Initial Basemail mailbox registry invalidation broadcast failed");
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await _mailboxRegistryService.SyncFromPeersAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Basemail mailbox registry sync iteration failed");
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }
}
