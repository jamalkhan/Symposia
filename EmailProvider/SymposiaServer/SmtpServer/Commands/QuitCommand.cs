namespace NativeSmtpReceiver;
public class QuitCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "QUIT" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        session.IsTerminated = true;
        await connection.WriteLineAsync("221 2.0.0 Bye");
    }
}
