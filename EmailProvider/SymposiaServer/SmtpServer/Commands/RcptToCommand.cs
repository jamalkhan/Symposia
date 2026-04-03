using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;
public class RcptToCommand : SmtpCommandBase
{
    private readonly HostingDirectory _hostingDirectory;
    private readonly ILogger<RcptToCommand> _logger;

    public RcptToCommand(HostingDirectory hostingDirectory, ILogger<RcptToCommand> logger)
    {
        _hostingDirectory = hostingDirectory;
        _logger = logger;
    }

    public override string[] SupportedVerbs => new[] { "RCPT" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        if (!session.HasGreeted)
        {
            await connection.WriteLineAsync("503 5.5.1 Send EHLO/HELO first");
            return;
        }

        if (string.IsNullOrWhiteSpace(session.MailFrom))
        {
            await connection.WriteLineAsync("503 5.5.1 Need MAIL FROM before RCPT TO");
            return;
        }

        if (argument is null || !argument.StartsWith("TO:", StringComparison.OrdinalIgnoreCase))
        {
            await connection.WriteLineAsync("501 5.5.4 Syntax: RCPT TO:<address>");
            return;
        }

        var rcptPart = argument["TO:".Length..].Trim();
        var rcpt = ParseAddress(rcptPart);

        if (string.IsNullOrWhiteSpace(rcpt))
        {
            await connection.WriteLineAsync("501 5.1.7 Invalid address");
            return;
        }

        if (!_hostingDirectory.TryResolveRecipient(rcpt, out var route, out var rejectionResponse))
        {
            _logger.LogWarning("Rejected recipient {Recipient}: {Reason}", rcpt, rejectionResponse);
            await connection.WriteLineAsync(rejectionResponse);
            return;
        }

        _logger.LogInformation(
            "Accepted recipient {Recipient} routed to mailbox {MailboxId} via provider {ProviderName}",
            route.Address,
            route.MailboxId,
            route.StorageProviderName);
        session.Recipients.Add(route);
        await connection.WriteLineAsync("250 2.1.5 Ok");
    }
}
