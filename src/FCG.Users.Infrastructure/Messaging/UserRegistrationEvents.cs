using FCG.Contracts;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using MassTransit;

namespace FCG.Users.Infrastructure.Messaging;

// IPublishEndpoint is scoped and intercepted by the EF Bus Outbox. Never inject IBus here.
public sealed class UserRegistrationEvents(IPublishEndpoint publisher) : IUserRegistrationEvents
{
    public Task PublishCreatedAsync(User user, CancellationToken cancellationToken)
    {
        var created = new DateTimeOffset(user.CreatedAtUtc);
        var message = new UserCreatedEvent
        {
            EventId = Guid.NewGuid(), UserId = user.Id, EventVersion = user.StateVersion,
            OccurredAtUtc = created, CreatedAtUtc = created, Name = user.Name, Email = user.Email.Value,
            Role = user.Role switch
            {
                UserRole.User => IdentityRole.User,
                UserRole.Administrator => IdentityRole.Administrator,
                _ => throw new InvalidOperationException("Unsupported identity role.")
            },
            State = user.IsActive && !user.IsDeleted ? IdentityState.Active
                : throw new InvalidOperationException("Registration requires an active account.")
        };
        IdentitySnapshotValidation.Validate(message);
        return publisher.Publish(message, context =>
        {
            context.MessageId = message.EventId;
            context.CorrelationId = user.Id;
        }, cancellationToken);
    }
}
