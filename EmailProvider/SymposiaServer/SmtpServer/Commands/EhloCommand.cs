namespace NativeSmtpReceiver;


// ────────────────────────────────────────────────
// Concrete command implementations
// ────────────────────────────────────────────────

public class EhloCommand : SmtpCommandBase
{
    public override string[] SupportedVerbs => new[] { "EHLO", "HELO" };

    public override async Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer)
    {
        await writer.WriteLineAsync("250-native-smtp.local Hello");
        await writer.WriteLineAsync("250-8BITMIME");
        await writer.WriteLineAsync("250-SIZE 10485760");
        await writer.WriteLineAsync("250 STARTTLS"); // change condition later if TLS ready
        await writer.FlushAsync();
    }
}