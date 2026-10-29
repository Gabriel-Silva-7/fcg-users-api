using System.Globalization;
using FCG.Users.Application.Common;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public sealed record AdminUserSummary(Guid Id, string Name, string Email, UserRole Role, bool IsActive,
    bool IsDeleted, DateTime CreatedAtUtc, string Version);
public enum ChangeUserStatusStatus { Updated, NotFound, ConcurrencyConflict, CannotDeactivateSelf, UserDeleted }
public sealed record ChangeUserStatusResult(ChangeUserStatusStatus Status, AdminUserSummary? User = null);
public sealed record ChangeUserStatusCommand(Guid ActorUserId, Guid UserId, bool IsActive, string? Version);
public enum DeleteUserStatus { Deleted, NotFound, CannotDeleteSelf }
public sealed record DeleteUserResult(DeleteUserStatus Status, bool Changed = false);
public sealed record DeleteUserCommand(Guid ActorUserId, Guid UserId);

public interface IUserAdministration
{
    Task<ChangeUserStatusResult> ChangeStatusAsync(Guid userId, bool isActive, uint expectedVersion, CancellationToken cancellationToken);
    Task<DeleteUserResult> DeleteAsync(Guid userId, CancellationToken cancellationToken);
}

public sealed class ChangeUserStatusHandler(IUserAdministration administration)
{
    public Task<ChangeUserStatusResult> HandleAsync(ChangeUserStatusCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!uint.TryParse(command.Version, NumberStyles.None, CultureInfo.InvariantCulture, out var version))
            throw new ApplicationValidationException(new Dictionary<string,string[]>(StringComparer.Ordinal)
                { [nameof(command.Version)] = ["Version must be an unsigned integer string."] });
        return !command.IsActive && command.ActorUserId == command.UserId
            ? Task.FromResult(new ChangeUserStatusResult(ChangeUserStatusStatus.CannotDeactivateSelf))
            : administration.ChangeStatusAsync(command.UserId, command.IsActive, version, cancellationToken);
    }
}

public sealed class DeleteUserHandler(IUserAdministration administration)
{
    public Task<DeleteUserResult> HandleAsync(DeleteUserCommand command, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        return command.ActorUserId == command.UserId ? Task.FromResult(new DeleteUserResult(DeleteUserStatus.CannotDeleteSelf))
            : administration.DeleteAsync(command.UserId, cancellationToken);
    }
}
