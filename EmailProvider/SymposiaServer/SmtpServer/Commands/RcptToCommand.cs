namespace NativeSmtpReceiver;
public class RcptToCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "RCPT" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer)
    {
        var rcptPart = argument?.TrimStart(new[] { 'T', 'O', ':' })?.Trim() ?? "";
        var rcpt = ParseAddress(rcptPart);

        if (!string.IsNullOrEmpty(rcpt))
        {
            session.Recipients.Add(rcpt);
            await writer.WriteLineAsync("250 2.1.5 Ok");
            await writer.FlushAsync();
        }
        else
        {
            await writer.WriteLineAsync("501 5.1.7 Invalid address");
            await writer.FlushAsync();
        }
    }
}