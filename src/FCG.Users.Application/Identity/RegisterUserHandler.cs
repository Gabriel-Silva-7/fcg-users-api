using FCG.Users.Application.Common;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public sealed class RegisterUserHandler(
    IUserRepository userRepository,
    IPasswordHasher passwordHasher,
    IClock clock)
{
    public async Task<RegisterUserResult> HandleAsync(
        RegisterUserCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (name, email, password) = Validate(command);

        if (await userRepository.ExistsByEmailAsync(email, cancellationToken))
        {
            return RegisterUserResult.EmailAlreadyRegistered();
        }

        var passwordHash = passwordHasher.Hash(password);
        var user = User.Register(name, email, passwordHash, clock.UtcNow);

        userRepository.Add(user);

        try
        {
            await userRepository.SaveChangesAsync(cancellationToken);
        }
        catch (EmailAlreadyRegisteredException)
        {
            return RegisterUserResult.EmailAlreadyRegistered();
        }

        return RegisterUserResult.Created(user);
    }

    private static (string Name, Email Email, string Password) Validate(RegisterUserCommand command)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var name = command.Name?.Trim();

        if (string.IsNullOrEmpty(name) || name.Length > User.MaxNameLength)
        {
            errors[nameof(command.Name)] =
                [$"Name must contain between 1 and {User.MaxNameLength} characters."];
        }

        if (!Email.TryCreate(command.Email, out var email))
        {
            errors[nameof(command.Email)] = ["Email format is invalid."];
        }

        if (command.Password is null)
        {
            errors[nameof(command.Password)] = ["Password is required."];
        }
        else
        {
            try
            {
                PasswordPolicy.EnsureIsValid(command.Password);
            }
            catch (ArgumentException exception)
            {
                errors[nameof(command.Password)] = [exception.Message];
            }
        }

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }

        return (name!, email!, command.Password!);
    }
}
