using Microsoft.Extensions.Hosting;

namespace InboxWeb;

public sealed class OutboundRelayWorker : BackgroundService
{
    private readonly InboxWebOptions _options;
    private readonly HostedMailboxRepository _mailboxRepository;
    private readonly MailboxContentStore _mailboxContentStore;
    private readonly OutboundRelayService _outboundRelayService;
    private readonly ILogger<OutboundRelayWorker> _logger;

    public OutboundRelayWorker(
        InboxWebOptions options,
        HostedMailboxRepository mailboxRepository,
        MailboxContentStore mailboxContentStore,
        OutboundRelayService outboundRelayService,
        ILogger<OutboundRelayWorker> logger)
    {
        _options = options;
        _mailboxRepository = mailboxRepository;
        _mailboxContentStore = mailboxContentStore;
        _outboundRelayService = outboundRelayService;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_outboundRelayService.IsEnabled)
        {
            _logger.LogInformation("Outbound relay worker is idle because no outbound relay host is configured");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var storageRoots = await _mailboxRepository.ListFileSystemStorageRootsAsync(stoppingToken);
                foreach (var storageRoot in storageRoots)
                {
                    var pending = await _mailboxContentStore.ListPendingOutboundAsync(storageRoot, stoppingToken);
                    foreach (var entry in pending)
                    {
                        try
                        {
                            await _outboundRelayService.SendAsync(entry.Message, stoppingToken);
                            await _mailboxContentStore.MarkOutboundDeliveredAsync(entry.QueuePath, entry.Message, stoppingToken);
                        }
                        catch (Exception ex)
                        {
                            _logger.LogWarning(ex, "Outbound relay failed for queued message {MessageId}", entry.Message.MessageId);
                            await _mailboxContentStore.MarkOutboundAttemptFailedAsync(
                                entry.QueuePath,
                                entry.Message,
                                ex.Message,
                                _options.OutboundMaxAttempts,
                                stoppingToken);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Outbound relay worker loop failed unexpectedly");
            }

            await Task.Delay(TimeSpan.FromSeconds(_options.OutboundPollSeconds), stoppingToken);
        }
    }
}
