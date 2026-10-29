using FCG.Contracts;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Application.Identity;

/// <summary>Stages identity state in the caller's transaction; never contacts the broker directly.</summary>
public interface IUserIdentityEvents
{
    Task PublishChangedAsync(User user, CancellationToken cancellationToken);
    Task PublishSnapshotAsync(UserStateSnapshotEvent snapshot, CancellationToken cancellationToken);
}
