namespace NativeSmtpReceiver;
public class QuitCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "QUIT" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer)
    {
        await writer.WriteLineAsync("221 2.0.0 Bye");
        await writer.FlushAsync();
        // To break loop: throw new Exception("QUIT") or use a session flag
        new OperationCanceledException("QUIT received");
    }
}