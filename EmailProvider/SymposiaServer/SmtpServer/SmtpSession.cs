namespace NativeSmtpReceiver;

/// <summary>
/// Session state (per connection
/// </summary>
public class SmtpSession
{
    public bool HasGreeted { get; set; }
    public bool IsTerminated { get; set; }
    public bool IsAuthenticated { get; set; }
    public string? MailFrom { get; set; }
    public string? AuthenticatedUser { get; set; }
    public List<string> Recipients { get; } = new();
    public List<string> DataLines { get; } = new();
    public bool InDataMode { get; set; }

    public void ResetTransaction()
    {
        MailFrom = null;
        Recipients.Clear();
        DataLines.Clear();
        InDataMode = false;
    }
}
