using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

public sealed class GetCurrentUserHandler(IUserRepository userRepository)
{
    public async Task<CurrentUser?> HandleAsync(
        Guid userId,
        CancellationToken cancellationToken)
    {
        var user = await userRepository.FindByIdAsync(userId, cancellationToken);

        return user is null || !user.IsActive || user.IsDeleted
            ? null
            : new CurrentUser(user.Id, user.Name, user.Email.Value, user.Role);
    }
}

public sealed record CurrentUser(Guid Id, string Name, string Email, UserRole Role);
