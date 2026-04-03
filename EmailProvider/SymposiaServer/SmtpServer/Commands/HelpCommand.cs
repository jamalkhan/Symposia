namespace NativeSmtpReceiver;

public sealed class HelpCommand : SmtpCommandBase
{
    private static readonly string[] HelpLines =
    {
        "214-Commands supported:",
        "214 EHLO HELO MAIL RCPT DATA RSET NOOP QUIT STARTTLS AUTH VRFY EXPN HELP"
    };

    public override string[] SupportedVerbs => new[] { "HELP" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        foreach (var line in HelpLines)
        {
            await connection.WriteLineAsync(line);
        }
    }
}
