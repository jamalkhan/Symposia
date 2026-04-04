using System.Net;
using System.Net.Mail;

namespace InboxWeb;

public sealed class OutboundRelayService
{
    private readonly InboxWebOptions _options;
    private readonly ILogger<OutboundRelayService> _logger;

    public OutboundRelayService(InboxWebOptions options, ILogger<OutboundRelayService> logger)
    {
        _options = options;
        _logger = logger;
    }

    public bool IsEnabled => _options.HasOutboundRelay;

    public async Task SendAsync(OutboundQueuedMessage queuedMessage, CancellationToken cancellationToken = default)
    {
        if (!IsEnabled || string.IsNullOrWhiteSpace(_options.OutboundRelayHost))
        {
            throw new InvalidOperationException("Outbound relay is not configured.");
        }

        using var smtpClient = new SmtpClient(_options.OutboundRelayHost, _options.OutboundRelayPort)
        {
            EnableSsl = _options.OutboundRelayUseSsl,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Credentials = string.IsNullOrWhiteSpace(_options.OutboundRelayUsername)
                ? CredentialCache.DefaultNetworkCredentials
                : new NetworkCredential(_options.OutboundRelayUsername, _options.OutboundRelayPassword)
        };

        using var message = new MailMessage
        {
            From = new MailAddress(queuedMessage.From),
            Subject = queuedMessage.Subject,
            Body = queuedMessage.PlainTextBody,
            IsBodyHtml = false
        };

        foreach (var recipient in queuedMessage.Recipients)
        {
            message.To.Add(recipient);
        }

        if (!string.IsNullOrWhiteSpace(queuedMessage.Cc))
        {
            foreach (var cc in queuedMessage.Cc.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                message.CC.Add(cc);
            }
        }

        if (!string.IsNullOrWhiteSpace(queuedMessage.Bcc))
        {
            foreach (var bcc in queuedMessage.Bcc.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
            {
                message.Bcc.Add(bcc);
            }
        }

        if (!string.IsNullOrWhiteSpace(queuedMessage.HtmlBody))
        {
            message.AlternateViews.Add(AlternateView.CreateAlternateViewFromString(queuedMessage.HtmlBody, null, "text/html"));
        }

        _logger.LogInformation(
            "Sending outbound relay message {MessageId} to {RecipientCount} recipients via {Host}:{Port}",
            queuedMessage.MessageId,
            queuedMessage.Recipients.Count,
            _options.OutboundRelayHost,
            _options.OutboundRelayPort);

        cancellationToken.ThrowIfCancellationRequested();
        await smtpClient.SendMailAsync(message, cancellationToken);
    }
}
