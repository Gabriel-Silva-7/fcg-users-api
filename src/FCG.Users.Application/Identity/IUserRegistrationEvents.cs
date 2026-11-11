using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

/// <summary>Stages a registration fact in the current user transaction, without network I/O.</summary>
public interface IUserRegistrationEvents
{
    Task PublishCreatedAsync(User user, CancellationToken cancellationToken);
}
