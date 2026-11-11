using System.Net;
using System.Net.Http.Json;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Identity;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Xunit;
using static FCG.Users.Api.Tests.Authentication.RegistrationTests;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class MessagingConfigurationTests(AuthFixture fixture)
{
    [Theory]
    [InlineData("RabbitMq:Host", "")]
    [InlineData("RabbitMq:Host", "amqp://user:synthetic-secret@localhost")]
    [InlineData("RabbitMq:Port", "0")]
    [InlineData("RabbitMq:VirtualHost", "/")]
    [InlineData("RabbitMq:Username", "")]
    [InlineData("RabbitMq:Password", "")]
    public async Task Invalid_broker_configuration_fails_startup_without_exposing_values(string key, string value)
    {
        await using var app = await fixture.CreateAsync();
        app.Overrides[key] = value;
        var error = Record.Exception(() => { using var client = app.CreateClient(); });
        Assert.NotNull(error);
        Assert.Contains("RabbitMq", error.ToString());
        Assert.DoesNotContain("synthetic-secret", error.ToString());
        Assert.DoesNotContain(fixture.BrokerSettings["RabbitMq:Password"]!, error.ToString());
    }

    [Fact]
    public async Task Additive_migration_preserves_existing_user_and_login_and_readiness_requires_outbox_schema()
    {
        await using var app = await fixture.CreateAsync();
        app.Overrides["Database:MigrateOnStartup"] = "false";
        using var client = app.CreateClient();
        var hash = new AspNetCorePasswordHasher().Hash(Password);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
            await db.GetService<IMigrator>().MigrateAsync("20261026220054_InitialUsers");
            db.Users.Add(User.Register("Person", Email.Create("person@example.com"), hash, DateTime.UtcNow));
            await db.SaveChangesAsync();
        }
        using var notReady = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.ServiceUnavailable, notReady.StatusCode);
        using var live = await client.GetAsync("/health/live");
        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
            Assert.False(db.Database.HasPendingModelChanges());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        using var ready = await client.GetAsync("/health/ready");
        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        Assert.Equal(hash, await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\""));
        using var login = await client.PostAsJsonAsync(LoginTests.Endpoint, new { email = "person@example.com", password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxState\""));
    }
}
