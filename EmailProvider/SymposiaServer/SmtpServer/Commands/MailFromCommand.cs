namespace NativeSmtpReceiver;

public class MailFromCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "MAIL" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        if (!session.HasGreeted)
        {
            await connection.WriteLineAsync("503 5.5.1 Send EHLO/HELO first");
            return;
        }

        if (argument is null || !argument.StartsWith("FROM:", StringComparison.OrdinalIgnoreCase))
        {
            await connection.WriteLineAsync("501 5.5.4 Syntax: MAIL FROM:<address>");
            return;
        }

        var address = ParseAddress(argument["FROM:".Length..].Trim());
        if (string.IsNullOrWhiteSpace(address))
        {
            await connection.WriteLineAsync("501 5.1.7 Invalid address");
            return;
        }

        session.ResetTransaction();
        session.HasGreeted = true;
        session.MailFrom = address;

        await connection.WriteLineAsync("250 2.1.0 Ok");
    }
}
