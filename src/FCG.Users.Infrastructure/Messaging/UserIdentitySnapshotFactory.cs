using FCG.Contracts;
using FCG.Users.Application.Common;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Infrastructure.Messaging;

public sealed class UserIdentitySnapshotFactory(IClock clock)
{
    public UserStateChangedEvent Changed(User user)
    {
        var result = new UserStateChangedEvent
        {
            EventId = Guid.NewGuid(), UserId = user.Id, EventVersion = user.StateVersion,
            OccurredAtUtc = Occurrence(), CreatedAtUtc = new(user.CreatedAtUtc),
            Name = user.Name, Email = user.Email.Value, Role = Role(user), State = State(user)
        };
        IdentitySnapshotValidation.Validate(result);
        return result;
    }
    public UserStateSnapshotEvent Snapshot(User user)
    {
        var result = new UserStateSnapshotEvent
        {
            EventId = Guid.NewGuid(), UserId = user.Id, EventVersion = user.StateVersion,
            OccurredAtUtc = Occurrence(), CreatedAtUtc = new(user.CreatedAtUtc),
            Name = user.Name, Email = user.Email.Value, Role = Role(user), State = State(user)
        };
        IdentitySnapshotValidation.Validate(result);
        return result;
    }
    private DateTimeOffset Occurrence()
    {
        var now = clock.UtcNow;
        if (now.Kind != DateTimeKind.Utc) throw new InvalidOperationException("Identity event clock must be UTC.");
        return new DateTimeOffset(now.Ticks - now.Ticks % 10, TimeSpan.Zero);
    }
    private static IdentityRole Role(User user) => user.Role switch
    {
        UserRole.User => IdentityRole.User, UserRole.Administrator => IdentityRole.Administrator,
        _ => throw new InvalidOperationException("Unsupported identity role.")
    };
    private static IdentityState State(User user) => user.IsDeleted ? IdentityState.Deleted : user.IsActive ? IdentityState.Active : IdentityState.Blocked;
}
