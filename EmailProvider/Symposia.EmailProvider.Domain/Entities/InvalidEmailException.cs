namespace Symposia.Domain.Entities;

public class InvalidEmailException : DomainException
{
    public InvalidEmailException(string message) 
        : base(message) { }
}