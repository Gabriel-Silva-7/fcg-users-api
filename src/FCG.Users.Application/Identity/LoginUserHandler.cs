using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public sealed class LoginUserHandler(
    IUserRepository userRepository,
    IPasswordHasher passwordHasher,
    IJwtTokenGenerator jwtTokenGenerator)
{
    public async Task<LoginUserResult> HandleAsync(
        LoginUserCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        User? user = null;

        if (Email.TryCreate(command.Email, out var email))
        {
            user = await userRepository.FindByEmailAsync(email, cancellationToken);
        }

        var passwordMatches = passwordHasher.Verify(user?.PasswordHash, command.Password ?? string.Empty);

        if (user is null || command.Password is null || !user.IsActive || user.IsDeleted || !passwordMatches)
        {
            return LoginUserResult.InvalidCredentials();
        }

        return LoginUserResult.Authenticated(jwtTokenGenerator.Generate(user));
    }
}
