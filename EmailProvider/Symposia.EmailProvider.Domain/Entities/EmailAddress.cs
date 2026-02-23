using System.Text.RegularExpressions;

namespace Symposia.Domain.Entities;

public readonly record struct EmailAddress
{
    private static readonly Regex EmailRegex = new(
        @"^[^@\s]+@[^@\s]+\.[^@\s]+$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    public string Value { get; }

    public EmailAddress(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("Email cannot be empty.", nameof(value));

        var trimmed = value.Trim().ToLowerInvariant();

        if (!EmailRegex.IsMatch(trimmed))
            throw new ArgumentException("Invalid email format.", nameof(value));

        Value = trimmed;
    }

    public static implicit operator string(EmailAddress email) => email.Value;
    public static implicit operator EmailAddress(string value) => new(value);

    public override string ToString() => Value;
}
