using Microsoft.Extensions.Logging;

namespace NativeSmtpReceiver;
public class RcptToCommand : SmtpCommandBase
{
    private readonly HostingDirectory _hostingDirectory;
    private readonly SmtpServerOptions _options;
    private readonly ILogger<RcptToCommand> _logger;

    public RcptToCommand(HostingDirectory hostingDirectory, SmtpServerOptions options, ILogger<RcptToCommand> logger)
    {
        _hostingDirectory = hostingDirectory;
        _options = options;
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
            if (string.Equals(rejectionResponse, "550 5.1.2 Domain not hosted here", StringComparison.Ordinal) &&
                (!session.IsAuthenticated || !_options.AllowAuthenticatedRelay))
            {
                rejectionResponse = "554 5.7.1 Relay access denied";
            }

            _logger.LogWarning("Rejected recipient {Recipient}: {Reason}", rcpt, rejectionResponse);
            await connection.WriteLineAsync(rejectionResponse);
            return;
        }

        if (session.Recipients.Count >= _options.MaxRecipientsPerMessage)
        {
            _logger.LogWarning("Rejected recipient {Recipient}: recipient limit exceeded", rcpt);
            await connection.WriteLineAsync("452 4.5.3 Too many recipients");
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
