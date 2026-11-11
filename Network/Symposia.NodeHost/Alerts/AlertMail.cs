using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;
using Symposia.NodeHost.Settings;

namespace Symposia.NodeHost.Alerts;

public sealed record AlertMailMessage(string To, string From, string Subject, string Body);

/// <summary>Sends one alert email (issue #196). SMTP for now; a Symposia EmailProvider transport can follow.</summary>
public interface IAlertMailTransport
{
    Task SendAsync(AlertMailMessage message, AlertSettings settings, string? password, CancellationToken ct);
}

public sealed class SmtpAlertMailTransport : IAlertMailTransport
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    public async Task SendAsync(AlertMailMessage message, AlertSettings settings, string? password, CancellationToken ct)
    {
        var mime = new MimeMessage();
        mime.From.Add(MailboxAddress.Parse(message.From));
        mime.To.Add(MailboxAddress.Parse(message.To));
        mime.Subject = message.Subject;
        mime.Body = new TextPart("plain") { Text = message.Body };

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(Timeout);
        using var client = new SmtpClient { Timeout = (int)Timeout.TotalMilliseconds };
        var security = settings.Security switch
        {
            SmtpSecurity.Tls => SecureSocketOptions.SslOnConnect,
            SmtpSecurity.None => SecureSocketOptions.None,
            _ => SecureSocketOptions.StartTls,
        };
        await client.ConnectAsync(settings.SmtpHost, settings.SmtpPort, security, timeout.Token);
        if (!string.IsNullOrEmpty(settings.Username))
        {
            await client.AuthenticateAsync(settings.Username, password ?? "", timeout.Token);
        }

        await client.SendAsync(mime, timeout.Token);
        await client.DisconnectAsync(true, timeout.Token);
    }
}

public static class AlertMail
{
    /// <summary>0x + first 4 + … + last 4 hex characters of the node address.</summary>
    public static string ShortAddress(string node) =>
        node.Length > 12 && node.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? $"{node[..6]}…{node[^4..]}" : node;

    /// <summary>A single bare address (no display name), the rule the Settings field enforces.</summary>
    public static bool IsValidAddress(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value != value.Trim() || value.Any(char.IsWhiteSpace))
        {
            return false;
        }

        return MailboxAddress.TryParse(value, out var parsed) && parsed.Address == value && parsed.Name is null or ""
            && value.IndexOf('@') is > 0 and var at && at < value.Length - 1 && value[(at + 1)..].Contains('.');
    }

    /// <summary>Errors for the Operator Alerts settings; empty email means alerts aren't emailed.</summary>
    public static List<string> Validate(AlertSettings a)
    {
        var errors = new List<string>();
        if (string.IsNullOrEmpty(a.Email))
        {
            return errors;
        }

        if (!IsValidAddress(a.Email))
        {
            errors.Add("Alert email address isn't a valid email address.");
        }

        if (!string.IsNullOrEmpty(a.From) && !IsValidAddress(a.From))
        {
            errors.Add("Alert From address isn't a valid email address.");
        }

        if (string.IsNullOrWhiteSpace(a.SmtpHost))
        {
            errors.Add("Operator Alerts need an SMTP server to send email.");
        }

        if (a.SmtpPort is < 1 or > 65535)
        {
            errors.Add("SMTP port must be between 1 and 65535.");
        }

        return errors;
    }

    /// <summary>Removes the password from an SMTP error before it is shown or logged.</summary>
    public static string Scrub(string text, string? password) =>
        string.IsNullOrEmpty(password) ? text : text.Replace(password, "********", StringComparison.Ordinal);
}
