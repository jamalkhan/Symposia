namespace NativeSmtpReceiver;

public class DataCommand : SmtpCommandBase
{
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

        await connection.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
    }
}
