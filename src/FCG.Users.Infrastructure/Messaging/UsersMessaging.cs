using FCG.Contracts;
using FCG.Users.Application.Identity;
using FCG.Users.Infrastructure.Persistence;
using MassTransit;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace FCG.Users.Infrastructure.Messaging;

public static class UsersMessaging
{
    public static IServiceCollection AddUsersMessaging(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("RabbitMq").Get<RabbitMqOptions>() ?? new();
        settings.Validate();
        services.AddSingleton(settings);
        services.AddScoped<IUserRegistrationEvents, UserRegistrationEvents>();
        services.AddScoped<UserIdentitySnapshotFactory>();
        services.AddScoped<IUserIdentityEvents, UserIdentityEvents>();
        services.AddMassTransit(bus =>
        {
            bus.ConfigureHealthCheckOptions(options =>
            {
                // The stock check can expose raw transport exceptions. Readiness uses
                // our sanitized check and probes the broker even when this bus is idle.
                options.Name = "users-bus-diagnostics";
                options.Tags.Clear();
                options.Tags.Add("diagnostic");
            });
            bus.AddEntityFrameworkOutbox<UsersDbContext>(outbox =>
            {
                outbox.UsePostgres();
                outbox.DisableInboxCleanupService(); // No incoming consumers in this increment.
                outbox.QueryDelay = TimeSpan.FromSeconds(1);
                outbox.UseBusOutbox(options => options.MessageDeliveryTimeout = TimeSpan.FromSeconds(5));
            });
            bus.UsingRabbitMq((_, rabbit) =>
            {
                rabbit.Host(settings.Host, settings.Port, settings.VirtualHost, host =>
                {
                    host.Username(settings.Username);
                    host.Password(settings.Password);
                    host.PublisherConfirmation = true;
                });
                rabbit.ConfigureJsonSerializerOptions(options =>
                {
                    options.Converters.Insert(0, new UserCreatedEventJsonConverter());
                    options.Converters.Insert(0, new IdentityEventJsonConverter<UserStateChangedEvent>());
                    options.Converters.Insert(0, new IdentityEventJsonConverter<UserStateSnapshotEvent>());
                    return options;
                });
            });
        });
        services.Configure<MassTransitHostOptions>(options =>
        {
            // HTTP can commit to the durable outbox before RabbitMQ becomes available.
            options.WaitUntilStarted = false;
            options.StopTimeout = TimeSpan.FromSeconds(5);
        });
        services.AddHealthChecks().AddCheck<UsersMessageTransportHealthCheck>("users-message-transport", tags: ["ready"]);
        return services;
    }
}
