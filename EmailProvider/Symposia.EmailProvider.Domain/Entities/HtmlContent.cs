namespace Symposia.Domain.Entities;

public readonly record struct HtmlContent
{
    public string Value { get; }

    public HtmlContent(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Content cannot be empty.");
        Value = value;
    }

    public override string ToString() => Value;
}
