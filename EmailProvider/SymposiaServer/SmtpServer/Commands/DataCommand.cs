namespace NativeSmtpReceiver;

public class DataCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "DATA" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer)
    {
        session.InDataMode = true;
        session.DataLines.Clear();

        await writer.WriteLineAsync("354 End data with <CR><LF>.<CR><LF>");
        await writer.FlushAsync();  // ← force send
    }
}