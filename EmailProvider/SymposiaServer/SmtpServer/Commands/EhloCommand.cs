namespace NativeSmtpReceiver;


// ────────────────────────────────────────────────
// Concrete command implementations
// ────────────────────────────────────────────────

public class EhloCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "EHLO", "HELO" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        session.HasGreeted = true;
        session.ResetTransaction();

        var authConfigured =
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_AUTH_USERNAME")) &&
            !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("SYMPOSIA_SMTP_AUTH_PASSWORD"));

        await connection.WriteLineAsync($"250-{connection.ServerName} Hello");
        await connection.WriteLineAsync("250-8BITMIME");
        await connection.WriteLineAsync("250-SIZE 10485760");

        if (connection.CanStartTls && !connection.IsTlsActive)
        {
            await connection.WriteLineAsync("250-STARTTLS");
        }

        if (authConfigured)
        {
            await connection.WriteLineAsync("250-AUTH PLAIN LOGIN");
        }

        await connection.WriteLineAsync("250 HELP");
    }
}
