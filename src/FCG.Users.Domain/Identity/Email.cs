using System.Diagnostics.CodeAnalysis;
using System.Net.Mail;

namespace FCG.Users.Domain.Identity;

public sealed record Email
{
    public const int MaxLength = 256;

    private Email(string value)
    {
        Value = value;
    }

    public string Value { get; }

    public static Email Create(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (!TryCreate(value, out var email))
        {
            throw new ArgumentException("Email format is invalid.", nameof(value));
        }

        return email;
    }

    public static bool TryCreate(string? value, [NotNullWhen(true)] out Email? email)
    {
        email = null;

        if (value is null)
        {
            return false;
        }

        var normalizedValue = value.Trim().ToLowerInvariant();

        if (normalizedValue.Length > MaxLength ||
            !MailAddress.TryCreate(normalizedValue, out var parsedEmail) ||
            parsedEmail.Address != normalizedValue)
        {
            return false;
        }

        email = new Email(normalizedValue);
        return true;
    }

    public override string ToString() => Value;
}
