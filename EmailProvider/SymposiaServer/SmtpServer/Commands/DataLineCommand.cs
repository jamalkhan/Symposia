using System;
using System.Collections.Generic;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;
public class DataLineCommand : ISmtpCommand   // special – handles lines while in DATA mode
{
    private readonly MailboxDeliveryService _deliveryService;
    private readonly MailboxRetryQueueService _retryQueueService;
    private readonly SmtpServerOptions _options;
    private readonly ILogger<DataLineCommand> _logger;

    public DataLineCommand(
        MailboxDeliveryService deliveryService,
        MailboxRetryQueueService retryQueueService,
        SmtpServerOptions options,
        ILogger<DataLineCommand> logger)
    {
        _deliveryService = deliveryService;
        _retryQueueService = retryQueueService;
        _options = options;
        _logger = logger;
    }

    public string[] SupportedVerbs => Array.Empty<string>(); // not verb-based
    public async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        if (fullLine == ".")
        {
            session.InDataMode = false;

            if (session.MessageSizeExceeded)
            {
                _logger.LogWarning(
                    "Rejected oversized message from {MailFrom} after reaching {MessageSizeBytes} bytes",
                    session.MailFrom,
                    session.CurrentMessageSizeBytes);
                session.ResetTransaction();
                await connection.WriteLineAsync("552 5.3.4 Message size exceeds fixed maximum message size");
                return;
            }

            try
            {
                await _deliveryService.StoreAsync(session.MailFrom ?? "unknown", session.Recipients, session.DataLines);
            }
            catch (TransientMailboxDeliveryException ex)
            {
                await _retryQueueService.EnqueueAsync(ex.Deliveries, ex.Message, default);

                _logger.LogWarning(
                    ex,
                    "Queued message from {MailFrom} for retry after transient delivery failure",
                    session.MailFrom);

                session.ResetTransaction();
                await connection.WriteLineAsync("250 2.0.0 Ok: queued for retry");
                return;
            }
            catch (PermanentMailboxDeliveryException ex)
            {
                _logger.LogWarning(
                    ex,
                    "Rejected message from {MailFrom} because delivery failed permanently",
                    session.MailFrom);
                session.ResetTransaction();
                await connection.WriteLineAsync("554 5.3.0 Message rejected");
                return;
            }

            _logger.LogInformation(
                "Queued message from {MailFrom} to {RecipientCount} recipients; preview: {Preview}",
                session.MailFrom,
                session.Recipients.Count,
                string.Join(" | ", session.DataLines.Take(3)));

            session.ResetTransaction();
            await connection.WriteLineAsync("250 2.0.0 Ok: queued");
            return;
        }

        string content = fullLine;
        if (content.StartsWith("..")) content = content[1..];
        session.CurrentMessageSizeBytes += Encoding.UTF8.GetByteCount(content) + 2;

        if (session.CurrentMessageSizeBytes > _options.MaxMessageSizeBytes)
        {
            session.MessageSizeExceeded = true;
            session.IsDiscardingData = true;
            return;
        }

        if (!session.IsDiscardingData)
        {
            session.DataLines.Add(content);
        }
    }
}
