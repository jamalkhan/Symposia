namespace Symposia.Domain.Entities;

public abstract class DomainException : Exception
{
    protected DomainException(string message) : base(message) { }
}
