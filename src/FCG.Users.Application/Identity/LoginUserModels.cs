namespace FCG.Users.Application.Identity;

public sealed record LoginUserCommand(string? Email, string? Password);

public sealed record AccessToken(string Value, int ExpiresInSeconds);

public enum LoginUserStatus
{
    Authenticated,
    InvalidCredentials,
}

public sealed record LoginUserResult(LoginUserStatus Status, AccessToken? Token)
{
    public static LoginUserResult Authenticated(AccessToken token) =>
        new(LoginUserStatus.Authenticated, token);

    public static LoginUserResult InvalidCredentials() =>
        new(LoginUserStatus.InvalidCredentials, null);
}
