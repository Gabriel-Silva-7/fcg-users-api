using FCG.Contracts;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using MassTransit;

namespace FCG.Users.Infrastructure.Messaging;

public sealed class UserIdentityEvents(IPublishEndpoint publisher, UserIdentitySnapshotFactory snapshots) : IUserIdentityEvents
{
    public Task PublishChangedAsync(User user, CancellationToken cancellationToken)
    {
        var snapshot = snapshots.Changed(user);
        return publisher.Publish(snapshot, context =>
        { context.MessageId = snapshot.EventId; context.CorrelationId = snapshot.UserId; }, cancellationToken);
    }
    public Task PublishSnapshotAsync(UserStateSnapshotEvent snapshot, CancellationToken cancellationToken)
    {
        IdentitySnapshotValidation.Validate(snapshot);
        return publisher.Publish(snapshot, context =>
        { context.MessageId = snapshot.EventId; context.CorrelationId = snapshot.UserId; }, cancellationToken);
    }
}
