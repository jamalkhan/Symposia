namespace Symposia.Domain.Entities;

public class Subscriber
{
    public Guid Id { get; private set; }
    public EmailAddress Email { get; private set; }
    public string? FirstName { get; private set; }
    public string? LastName { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? ConfirmedAt { get; private set; }
    public bool IsSubscribed { get; private set; }
    public string? Status { get; private set; }
    public List<Guid> SegmentIds { get; private set; } = new();
    private Subscriber() { }
    public static Subscriber Create(
        EmailAddress email,
        string? firstName = null,
        string? lastName = null)
    {
        //ArgumentNullException.ThrowIfNull(email);
        
        return new Subscriber
        {
            Id = Guid.NewGuid(),
            Email = email,
            FirstName = firstName,
            LastName = lastName,
            CreatedAt = DateTime.UtcNow,
            IsSubscribed = true,
            Status = "pending"
        };
    }

    public void ConfirmSubscription()
    {
        if (IsSubscribed)
        {
            ConfirmedAt = DateTime.UtcNow;
            Status = "active";
        }
    }

    public void Unsubscribe()
    {
        IsSubscribed = false;
        Status = "unsubscribed";
    }
}