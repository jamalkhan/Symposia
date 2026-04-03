namespace NativeSmtpReceiver;

public sealed class StartTlsCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "STARTTLS" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        if (!session.HasGreeted)
        {
            await connection.WriteLineAsync("503 5.5.1 Send EHLO/HELO first");
            return;
        }

        if (connection.IsTlsActive)
        {
            await connection.WriteLineAsync("503 5.5.1 TLS already active");
            return;
        }

        if (!connection.CanStartTls)
        {
            await connection.WriteLineAsync("454 4.7.0 TLS not available due to temporary reason");
            return;
        }

        await connection.WriteLineAsync("220 2.0.0 Ready to start TLS");
        await connection.UpgradeToTlsAsync();

        session.HasGreeted = false;
        session.IsAuthenticated = false;
        session.AuthenticatedUser = null;
        session.ResetTransaction();
    }
}
