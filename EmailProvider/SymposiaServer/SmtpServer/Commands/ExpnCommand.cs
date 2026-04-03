namespace NativeSmtpReceiver;

public sealed class ExpnCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "EXPN" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            await connection.WriteLineAsync("501 5.5.4 Syntax: EXPN mailing-list");
            return;
        }

        await connection.WriteLineAsync("252 2.5.2 Cannot expand mailing lists");
    }
}
