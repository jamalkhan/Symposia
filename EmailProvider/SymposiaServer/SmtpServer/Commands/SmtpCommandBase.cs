namespace NativeSmtpReceiver;

   

    

// ────────────────────────────────────────────────
// Base command (optional – for shared behavior)
// ────────────────────────────────────────────────
public abstract class SmtpCommandBase : ISmtpCommand
{
    public abstract string[] SupportedVerbs { get; }

    public abstract Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer);

    protected static string ParseAddress(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        if (s.StartsWith("<") && s.EndsWith(">")) s = s[1..^1];
        return s;
    }
}