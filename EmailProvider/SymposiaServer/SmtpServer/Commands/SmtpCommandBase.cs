namespace NativeSmtpReceiver;
using System.Text;

// ────────────────────────────────────────────────
// Base command (optional – for shared behavior)
// ────────────────────────────────────────────────
public abstract class SmtpCommandBase : ISmtpCommand
{
    public abstract string[] SupportedVerbs { get; }

    public abstract Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, SmtpConnectionContext connection);

    protected static string ParseAddress(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return "";
        s = s.Trim();
        if (s.StartsWith("<") && s.EndsWith(">")) s = s[1..^1];
        return s;
    }

    protected static string DecodeBase64(string value)
    {
        var bytes = Convert.FromBase64String(value);
        return Encoding.UTF8.GetString(bytes);
    }
}
