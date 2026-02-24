
namespace NativeSmtpReceiver;

/// <summary>
/// Command interface
/// </summary>
public interface ISmtpCommand
{
    string[] SupportedVerbs { get; }
    Task ExecuteAsync(string fullLine, string? argument, SmtpSession session, StreamWriter writer);
}
