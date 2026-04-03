using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;

public class DataCommand : SmtpCommandBase
{
    private readonly ILogger<DataCommand> _logger;

    public DataCommand(ILogger<DataCommand> logger)
    {
        _logger = logger;
    }

    public override string[] SupportedVerbs => new[] { "DATA" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        if (!session.HasGreeted)
        {
            await connection.WriteLineAsync("503 5.5.1 Send EHLO/HELO first");
            return;
        }

        if (string.IsNullOrWhiteSpace(session.MailFrom))
        {
            await connection.WriteLineAsync("503 5.5.1 Need MAIL FROM before DATA");
            return;
        }

        if (session.Recipients.Count == 0)
        {
            await connection.WriteLineAsync("503 5.5.1 Need RCPT TO before DATA");
            return;
        }

        session.InDataMode = true;
        session.DataLines.Clear();
        _logger.LogDebug("Entering DATA mode for sender {MailFrom} with {RecipientCount} recipients", session.MailFrom, session.Recipients.Count);

        await connection.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
    }
}
