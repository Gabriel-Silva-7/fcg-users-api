using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public sealed record RegisterUserCommand(string? Name, string? Email, string? Password);

public enum RegisterUserStatus
{
    Created,
    EmailAlreadyRegistered,
}

public sealed record RegisteredUser(Guid Id, string Name, string Email, UserRole Role);

public sealed record RegisterUserResult(RegisterUserStatus Status, RegisteredUser? User)
{
    public static RegisterUserResult Created(User user) =>
        new(
            RegisterUserStatus.Created,
            new RegisteredUser(user.Id, user.Name, user.Email.Value, user.Role));

    public static RegisterUserResult EmailAlreadyRegistered() =>
        new(RegisterUserStatus.EmailAlreadyRegistered, null);
}
