namespace NativeSmtpReceiver;

public sealed class VrfyCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "VRFY" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection)
    {
        if (string.IsNullOrWhiteSpace(argument))
        {
            await connection.WriteLineAsync("501 5.5.4 Syntax: VRFY mailbox");
            return;
        }

        await connection.WriteLineAsync($"252 2.1.5 Cannot VRFY user, but will accept message for <{argument}>");
    }
}
