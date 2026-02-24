   namespace NativeSmtpReceiver;
   
public class RsetCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "RSET" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer)
    {
        session.MailFrom = null;
        session.Recipients.Clear();
        session.DataLines.Clear();
        session.InDataMode = false;
        await writer.WriteLineAsync("250 2.0.0 Ok");
        await writer.FlushAsync();
    }
}

public class NoopCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "NOOP" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer)
    {
        await writer.WriteLineAsync("250 2.0.0 Ok");
        await writer.FlushAsync();
    }
}

public class UnknownCommand : ISmtpCommand
{
    public string[] SupportedVerbs => Array.Empty<string>();

    public async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer)
    {
        await writer.WriteLineAsync("502 Command not implemented");
        await writer.FlushAsync();
    }
}
