namespace NativeSmtpReceiver;

    /// <summary>
/// Session state (per connection
/// </summary>
public class SmtpSession
{
    public string? MailFrom { get; set; }
    public List<string> Recipients { get; } = new();
    public List<string> DataLines { get; } = new();
    public bool InDataMode { get; set; }
    // Future: public bool IsAuthenticated { get; set; }
    //         public string? AuthenticatedUser { get; set; }
}
