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
    public List<MailboxRoute> Recipients { get; } = new();
    public List<string> DataLines { get; } = new();
    public bool InDataMode { get; set; }
    public bool IsDiscardingData { get; set; }
    public bool MessageSizeExceeded { get; set; }
    public int CurrentMessageSizeBytes { get; set; }
    public int CommandCount { get; set; }
    public string RemoteIpAddress { get; set; } = "unknown";

    public void ResetTransaction()
    {
        MailFrom = null;
        Recipients.Clear();
        DataLines.Clear();
        InDataMode = false;
        IsDiscardingData = false;
        MessageSizeExceeded = false;
        CurrentMessageSizeBytes = 0;
    }
}
