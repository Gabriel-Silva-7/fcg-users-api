using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using System.Data.Common;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Contracts;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Xunit;
using static FCG.Users.Api.Tests.Authentication.RegistrationTests;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class RegistrationOutboxTests(AuthFixture fixture)
{
    [Fact]
    public async Task Registration_commits_user_and_one_durable_intent_with_delivery_disabled()
    {
        await using var app = await fixture.CreateAsync(customize: DisableDelivery);
        using var client = app.CreateClient();
        using var response = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.IsType<string>(await app.ScalarAsync("SELECT to_regclass('\"OutboxMessage\"')::text"));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxState\""));
        Assert.DoesNotContain(app.Services.GetServices<IHostedService>(),
            service => service.GetType().Name.StartsWith("BusOutboxDeliveryService", StringComparison.Ordinal));
        var user = (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        var body = Assert.IsType<string>(await app.ScalarAsync("SELECT \"Body\" FROM \"OutboxMessage\""));
        using var envelope = JsonDocument.Parse(body);
        var message = IdentityContractJson.Read<UserCreatedEvent>(envelope.RootElement.GetProperty("message").GetRawText());
        Assert.Equal(user, message.UserId);
        Assert.NotEqual(Guid.Empty, message.EventId);
        Assert.Equal(1, message.EventVersion);
        Assert.Equal("Person", message.Name);
        Assert.Equal("person@example.com", message.Email);
        Assert.Equal(IdentityRole.User, message.Role);
        Assert.Equal(IdentityState.Active, message.State);
        Assert.Equal(message.CreatedAtUtc, message.OccurredAtUtc);
        Assert.Equal(message.EventId, await app.ScalarAsync("SELECT \"MessageId\" FROM \"OutboxMessage\""));
        Assert.Equal(user, await app.ScalarAsync("SELECT \"CorrelationId\" FROM \"OutboxMessage\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\" m JOIN \"OutboxState\" s ON m.\"OutboxId\" = s.\"OutboxId\""));
        Assert.Contains("urn:message:FCG.Contracts:UserCreatedEvent", body);
        Assert.DoesNotContain(Password, body);
        Assert.DoesNotContain("passwordHash", body, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("accessToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Failure_after_save_before_commit_rolls_back_user_and_intent_and_allows_retry()
    {
        var failure = new FailCommitOnce();
        await using var app = await fixture.CreateAsync(customize: services =>
            services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(failure)));
        using var client = app.CreateClient();
        failure.Armed = true;
        using var response = await RegisterAsync(client);
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.True(failure.SawBothWritesInTransaction);
        foreach (var table in new[] { "Users", "OutboxMessage", "OutboxState" })
            Assert.Equal(0L, await app.ScalarAsync($"SELECT count(*) FROM \"{table}\""));
        using var retried = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, retried.StatusCode);
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.DoesNotContain("   at ", string.Join('\n', app.Logs.Entries));
    }

    [Fact]
    public async Task Failure_while_staging_event_does_not_persist_the_user()
    {
        await using var app = await fixture.CreateAsync(customize: services =>
        {
            services.RemoveAll<IUserRegistrationEvents>();
            services.AddScoped<IUserRegistrationEvents, FailingEvents>();
        });
        using var client = app.CreateClient();
        using var response = await RegisterAsync(client);
        await AssertProblemAsync(response, HttpStatusCode.InternalServerError, "internal_error");
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Invalid_and_duplicate_registrations_do_not_create_more_intents()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var first = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, first.StatusCode);
        using var duplicate = await RegisterAsync(client, "  PERSON@EXAMPLE.COM  ");
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "email_already_registered");
        using var invalid = await client.PostAsJsonAsync(Endpoint, new { name = "Name", email = "not-email", password = Password });
        await AssertProblemAsync(invalid, HttpStatusCode.BadRequest, "validation_error");
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxState\""));
    }

    internal static void DisableDelivery(IServiceCollection services)
    {
        foreach (var service in services.Where(descriptor => descriptor.ServiceType == typeof(IHostedService)
                     && descriptor.ImplementationType?.Name.StartsWith("BusOutboxDeliveryService", StringComparison.Ordinal) == true).ToArray())
            services.Remove(service);
    }

    private sealed class FailingEvents : IUserRegistrationEvents
    {
        public Task PublishCreatedAsync(User user, CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Synthetic event failure.");
    }

    private sealed class FailCommitOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public bool SawBothWritesInTransaction { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result;
            Armed = false;
            await using var command = transaction.Connection!.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "SELECT (SELECT count(*) FROM \"Users\") = 1 AND (SELECT count(*) FROM \"OutboxMessage\") = 1";
            SawBothWritesInTransaction = (bool)(await command.ExecuteScalarAsync(cancellationToken))!;
            throw new InvalidOperationException("Synthetic failure after save, before commit.");
        }
    }
}
