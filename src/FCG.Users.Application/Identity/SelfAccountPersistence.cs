using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public enum SelfSaveStatus { Saved, NotActive, EmailAlreadyRegistered, ConcurrencyConflict }

public interface IUserSelfRepository
{
    Task<User?> FindForUpdateAsync(Guid userId, CancellationToken cancellationToken);
    Task<SelfSaveStatus> SaveAsync(User user, CancellationToken cancellationToken);
}
