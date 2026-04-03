namespace NativeSmtpReceiver;

public sealed class TransientMailboxDeliveryException : Exception
{
    public TransientMailboxDeliveryException(string message, IReadOnlyList<MailboxStorageDelivery> deliveries, Exception innerException)
        : base(message, innerException)
    {
        Deliveries = deliveries;
    }

    public IReadOnlyList<MailboxStorageDelivery> Deliveries { get; }
}

public sealed class PermanentMailboxDeliveryException : Exception
{
    public PermanentMailboxDeliveryException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
