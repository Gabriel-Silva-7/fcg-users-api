using FCG.Users.Application.Common;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public sealed record UpdateCurrentUserCommand(Guid UserId, string? Name, string? Email);

public enum UpdateCurrentUserStatus
{
    Updated,
    NotFound,
    EmailAlreadyRegistered,
    ConcurrencyConflict,
}

public sealed record UpdateCurrentUserResult(
    UpdateCurrentUserStatus Status,
    CurrentUser? User = null);

public enum ProfileSaveStatus { Saved, NotActive, EmailAlreadyRegistered, ConcurrencyConflict }

public interface IUserProfileRepository
{
    Task<User?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken);
    Task<ProfileSaveStatus> SaveAsync(User user, CancellationToken cancellationToken);
}

public sealed class UpdateCurrentUserHandler(IUserProfileRepository userRepository)
{
    public async Task<UpdateCurrentUserResult> HandleAsync(
        UpdateCurrentUserCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);

        var (name, email) = Validate(command);
        var user = await userRepository.FindForUpdateAsync(
            command.UserId,
            cancellationToken);

        if (user is null || !user.IsActive || user.IsDeleted)
        {
            return new UpdateCurrentUserResult(UpdateCurrentUserStatus.NotFound);
        }

        user.UpdateProfile(name, email);

        var saved = await userRepository.SaveAsync(user, cancellationToken);
        if (saved != ProfileSaveStatus.Saved)
        {
            return new(saved switch
            {
                ProfileSaveStatus.NotActive => UpdateCurrentUserStatus.NotFound,
                ProfileSaveStatus.EmailAlreadyRegistered => UpdateCurrentUserStatus.EmailAlreadyRegistered,
                ProfileSaveStatus.ConcurrencyConflict => UpdateCurrentUserStatus.ConcurrencyConflict,
                _ => throw new InvalidOperationException("Unknown profile save result.")
            });
        }

        return new UpdateCurrentUserResult(
            UpdateCurrentUserStatus.Updated,
            new CurrentUser(user.Id, user.Name, user.Email.Value, user.Role));
    }

    private static (string Name, Email Email) Validate(UpdateCurrentUserCommand command)
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

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }

        return (name!, email!);
    }
}
