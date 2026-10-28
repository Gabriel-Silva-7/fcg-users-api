namespace FCG.Users.Domain.Identity;

public static class PasswordPolicy
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 128;

    public static void EnsureIsValid(string password)
    {
        ArgumentNullException.ThrowIfNull(password);

        if (password.Length < MinimumLength)
        {
            throw new ArgumentException(
                $"Password must contain at least {MinimumLength} characters.",
                nameof(password));
        }

        if (password.Length > MaximumLength)
        {
            throw new ArgumentException(
                $"Password cannot exceed {MaximumLength} characters.",
                nameof(password));
        }

        if (!password.Any(char.IsLetter))
        {
            throw new ArgumentException(
                "Password must contain at least one letter.",
                nameof(password));
        }

        if (!password.Any(char.IsDigit))
        {
            throw new ArgumentException(
                "Password must contain at least one digit.",
                nameof(password));
        }

        if (!password.Any(IsSpecialCharacter))
        {
            throw new ArgumentException(
                "Password must contain at least one special character.",
                nameof(password));
        }
    }

    private static bool IsSpecialCharacter(char character) =>
        !char.IsLetterOrDigit(character) && !char.IsWhiteSpace(character);
}
