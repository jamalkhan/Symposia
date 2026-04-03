   namespace NativeSmtpReceiver;
   
public class RsetCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "RSET" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        session.ResetTransaction();
        await connection.WriteLineAsync("250 2.0.0 Ok");
    }
}

public class NoopCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "NOOP" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        await connection.WriteLineAsync("250 2.0.0 Ok");
    }
}

public class UnknownCommand : ISmtpCommand
{
    public string[] SupportedVerbs => Array.Empty<string>();

    public async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        await connection.WriteLineAsync("502 5.5.1 Command not implemented");
    }
}
