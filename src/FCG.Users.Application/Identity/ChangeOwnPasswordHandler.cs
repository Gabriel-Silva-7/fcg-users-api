using FCG.Users.Application.Common;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public sealed record ChangeOwnPasswordCommand(
    Guid UserId,
    string? CurrentPassword,
    string? NewPassword);

public enum ChangeOwnPasswordStatus
{
    Updated,
    NotFound,
    InvalidCurrentPassword,
    ConcurrencyConflict,
}

public sealed record ChangeOwnPasswordResult(ChangeOwnPasswordStatus Status);

public sealed class ChangeOwnPasswordHandler(
    IUserSelfRepository userRepository,
    IPasswordHasher passwordHasher)
{
    public async Task<ChangeOwnPasswordResult> HandleAsync(
        ChangeOwnPasswordCommand command,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        Validate(command);

        var user = await userRepository.FindForUpdateAsync(
            command.UserId,
            cancellationToken);

        if (user is null || !user.IsActive || user.IsDeleted)
        {
            return new ChangeOwnPasswordResult(ChangeOwnPasswordStatus.NotFound);
        }

        if (!passwordHasher.Verify(user.PasswordHash, command.CurrentPassword!))
        {
            return new ChangeOwnPasswordResult(ChangeOwnPasswordStatus.InvalidCurrentPassword);
        }

        user.ChangePasswordHash(passwordHasher.Hash(command.NewPassword!));
        var saved = await userRepository.SaveAsync(user, cancellationToken);
        return new(saved switch
        {
            SelfSaveStatus.Saved => ChangeOwnPasswordStatus.Updated,
            SelfSaveStatus.NotActive => ChangeOwnPasswordStatus.NotFound,
            SelfSaveStatus.ConcurrencyConflict => ChangeOwnPasswordStatus.ConcurrencyConflict,
            _ => throw new InvalidOperationException("Unexpected password persistence result.")
        });
    }

    private static void Validate(ChangeOwnPasswordCommand command)
    {
        var errors = new Dictionary<string, string[]>(StringComparer.Ordinal);

        if (string.IsNullOrEmpty(command.CurrentPassword) || command.CurrentPassword.Length > PasswordPolicy.MaximumLength)
        {
            errors[nameof(command.CurrentPassword)] = [$"Current password must contain between 1 and {PasswordPolicy.MaximumLength} characters."];
        }

        if (command.NewPassword is null)
        {
            errors[nameof(command.NewPassword)] = ["New password is required."];
        }
        else
        {
            try
            {
                PasswordPolicy.EnsureIsValid(command.NewPassword);
            }
            catch (ArgumentException exception)
            {
                errors[nameof(command.NewPassword)] = [exception.Message];
            }
        }

        if (errors.Count > 0)
        {
            throw new ApplicationValidationException(errors);
        }
    }
}
