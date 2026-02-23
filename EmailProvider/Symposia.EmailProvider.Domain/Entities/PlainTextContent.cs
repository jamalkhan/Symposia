namespace Symposia.Domain.Entities;

public readonly record struct PlainTextContent
{
    public string Value { get; }

    public PlainTextContent(string value)
    {
        Value = value ?? throw new ArgumentNullException(nameof(value));
    }

    public override string ToString() => Value;
}
