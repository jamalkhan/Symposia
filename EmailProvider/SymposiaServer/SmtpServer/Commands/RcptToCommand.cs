namespace NativeSmtpReceiver;
public class RcptToCommand : SmtpCommandBase
{
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

        if (!string.IsNullOrEmpty(rcpt))
        {
            session.Recipients.Add(rcpt);
            await connection.WriteLineAsync("250 2.1.5 Ok");
        }
        else
        {
            await connection.WriteLineAsync("501 5.1.7 Invalid address");
        }
    }
}
