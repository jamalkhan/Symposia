using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;
public class DataLineCommand : ISmtpCommand   // special – handles lines while in DATA mode
{
    private readonly MailboxDeliveryService _deliveryService;
    private readonly ILogger<DataLineCommand> _logger;

    public DataLineCommand(MailboxDeliveryService deliveryService, ILogger<DataLineCommand> logger)
    {
        _deliveryService = deliveryService;
        _logger = logger;
    }

    public string[] SupportedVerbs => Array.Empty<string>(); // not verb-based
    public async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        if (fullLine == ".")
        {
            session.InDataMode = false;

            await _deliveryService.StoreAsync(session.MailFrom ?? "unknown", session.Recipients, session.DataLines);

            _logger.LogInformation(
                "Queued message from {MailFrom} to {RecipientCount} recipients; preview: {Preview}",
                session.MailFrom,
                session.Recipients.Count,
                string.Join(" | ", session.DataLines.Take(3)));

            session.DataLines.Clear();
            session.MailFrom = null;
            session.Recipients.Clear();
            await connection.WriteLineAsync("250 2.0.0 Ok: queued");
            return;
        }

        string content = fullLine;
        if (content.StartsWith("..")) content = content[1..];
        session.DataLines.Add(content);
    }
}
