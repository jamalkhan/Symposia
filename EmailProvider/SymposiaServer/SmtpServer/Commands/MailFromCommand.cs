namespace NativeSmtpReceiver;

public class MailFromCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "MAIL" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer)
    {
        session.MailFrom = ParseAddress(argument);
        await writer.WriteLineAsync($"250 2.1.0 Ok");
        await writer.FlushAsync();
    }
}