using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public sealed class MailboxRetryWorker : BackgroundService
{
    private readonly MailboxRetryQueueService _retryQueueService;
    private readonly MailboxDeliveryService _deliveryService;
    private readonly SmtpServerOptions _options;
    private readonly ILogger<MailboxRetryWorker> _logger;

    public MailboxRetryWorker(
        MailboxRetryQueueService retryQueueService,
        MailboxDeliveryService deliveryService,
        SmtpServerOptions options,
        ILogger<MailboxRetryWorker> logger)
    {
        _retryQueueService = retryQueueService;
        _deliveryService = deliveryService;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            await ProcessQueueAsync(stoppingToken);

            try
            {
                await Task.Delay(_options.RetryInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task ProcessQueueAsync(CancellationToken cancellationToken)
    {
        foreach (var path in _retryQueueService.GetPendingFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var item = await _retryQueueService.ReadAsync(path, cancellationToken);
            if (item is null)
            {
                continue;
            }

            try
            {
                await _deliveryService.StorePreparedDeliveriesAsync(item.Deliveries, cancellationToken);
                _retryQueueService.Complete(path);
                _logger.LogInformation(
                    "Successfully replayed queued delivery {QueueId} after {AttemptCount} prior attempts",
                    item.QueueId,
                    item.AttemptCount);
            }
            catch (TransientMailboxDeliveryException ex)
            {
                _logger.LogWarning(ex, "Retry delivery {QueueId} failed transiently", item.QueueId);
                await _retryQueueService.RequeueAsync(item, path, cancellationToken);
            }
            catch (PermanentMailboxDeliveryException ex)
            {
                _logger.LogWarning(ex, "Retry delivery {QueueId} failed permanently and was moved to dead-letter", item.QueueId);
                _retryQueueService.DeadLetter(path);
            }
        }
    }
}
