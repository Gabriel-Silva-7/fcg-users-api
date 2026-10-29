using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using System.Data.Common;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Identity;
using FCG.Users.Infrastructure.Persistence;
using FCG.Users.Infrastructure.Messaging;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class BootstrapTests(AuthFixture fixture)
{
    [Fact]
    public async Task Explicit_production_bootstrap_provisions_login_capable_accounts_and_canonical_events()
    {
        await using var app = await fixture.CreateAsync(); Configure(app); using var client = app.CreateClient();
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        using var adminLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = AdminEmail, password = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode); var adminToken = (await adminLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        Assert.Equal("Administrator", new JwtSecurityTokenHandler().ReadJwtToken(adminToken).Claims.Single(x => x.Type == "role").Value);
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", adminToken);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/test-auth/admin")).StatusCode);
        using var playerLogin = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = PlayerEmail, password = PlayerPassword });
        Assert.Equal(HttpStatusCode.OK, playerLogin.StatusCode); var playerToken = (await playerLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", playerToken);
        Assert.Equal(HttpStatusCode.Forbidden, (await client.GetAsync("/test-auth/admin")).StatusCode);
        var bodies = await app.ScalarAsync("SELECT string_agg(\"Body\", E'\\n') FROM \"OutboxMessage\"");
        Assert.Contains(AdminEmail, (string)bodies!); Assert.Contains(PlayerEmail, (string)bodies!);
        Assert.DoesNotContain(AdminPassword, (string)bodies!); Assert.DoesNotContain(PlayerPassword, (string)bodies!);
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\" WHERE \"Body\"::jsonb->'message'->>'eventVersion'='1' AND \"Body\"::jsonb->'message'->>'state'='Active'"));
        var logs = string.Join('\n', app.Logs.Entries);
        foreach (var secret in new[] { AdminEmail, PlayerEmail, AdminPassword, PlayerPassword, adminToken, playerToken }) Assert.DoesNotContain(secret, logs);
        Assert.Equal(2, app.Logs.Entries.Count(x => x.Contains("UserRegistered")));
    }

    [Fact]
    public async Task Disabled_bootstrap_ignores_partial_configuration_and_creates_nothing()
    {
        await using var app = await fixture.CreateAsync(); app.Overrides["AdminBootstrap:Email"] = "invalid-partial";
        using var client = app.CreateClient();
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Users\"")); Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Theory]
    [InlineData("Email", "")]
    [InlineData("Password", "")]
    [InlineData("PlayerEmail", "")]
    [InlineData("PlayerPassword", "")]
    [InlineData("Email", "invalid")]
    [InlineData("PlayerEmail", "invalid")]
    [InlineData("Password", "weak")]
    [InlineData("PlayerPassword", "weak")]
    [InlineData("PlayerEmail", "  BOOTSTRAP-ADMIN@EXAMPLE.COM  ")]
    public async Task Enabled_bootstrap_rejects_missing_invalid_or_duplicate_credentials_without_echoing_them(string field, string value)
    {
        await using var app = await fixture.CreateAsync(); Configure(app); app.Overrides["AdminBootstrap:" + field] = value;
        var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        Assert.DoesNotContain(AdminPassword, error.ToString()); Assert.DoesNotContain(PlayerPassword, error.ToString());
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
    }

    [Fact]
    public async Task New_host_with_changed_config_password_keeps_ids_hashes_and_only_original_events()
    {
        await using var app = await fixture.CreateAsync(); Configure(app); using var client = app.CreateClient();
        var original = await app.ScalarAsync("SELECT string_agg(\"Id\"::text || ':' || \"PasswordHash\", ',' ORDER BY \"Email\") FROM \"Users\"");
        await app.DisposeAsync();
        await using var restarted = new AuthFactory(app.ConnectionString, fixture.KeyDirectory, brokerSettings: fixture.BrokerSettings);
        Configure(restarted); restarted.Overrides["AdminBootstrap:Password"] = "Changed!Config2026";
        Task<HttpClient> starting; using (ExecutionContext.SuppressFlow()) starting = Task.Run(() => restarted.CreateClient());
        using var restartedClient = await starting;
        Assert.Equal(original, await restarted.ScalarAsync("SELECT string_agg(\"Id\"::text || ':' || \"PasswordHash\", ',' ORDER BY \"Email\") FROM \"Users\""));
        Assert.Equal(2L, await restarted.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        using var oldPassword = await restartedClient.PostAsJsonAsync("/api/v1/auth/login", new { email = AdminEmail, password = AdminPassword });
        Assert.Equal(HttpStatusCode.OK, oldPassword.StatusCode);
        Assert.DoesNotContain(restarted.Logs.Entries, x => x.Contains("UserRegistered"));
    }

    [Theory]
    [InlineData("User")]
    [InlineData("Blocked")]
    [InlineData("Deleted")]
    public async Task Conflicting_administrator_is_never_promoted_reactivated_or_restored(string state)
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>(); var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            var user = state == "User" ? User.Register("Existing", Email.Create(AdminEmail), hasher.Hash(AdminPassword), DateTime.UtcNow)
                : User.RegisterAdministrator("Existing", Email.Create(AdminEmail), hasher.Hash(AdminPassword), DateTime.UtcNow);
            if (state != "User") user.ChangeActiveStatus(false); db.Users.Add(user); await db.SaveChangesAsync();
        }
        if (state == "Deleted") await app.ExecuteAsync("UPDATE \"Users\" SET \"IsDeleted\"=true, \"IsActive\"=false");
        var before = await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\""); EnableInMemoryOptions(app);
        using var execution = app.Services.CreateScope();
        await Assert.ThrowsAsync<AdminBootstrapConflictException>(() => execution.ServiceProvider.GetRequiredService<AdminBootstrapService>().ExecuteAsync(CancellationToken.None));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\"")); Assert.Equal(before, await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(state == "User" ? "User" : "Administrator", await app.ScalarAsync("SELECT \"Role\" FROM \"Users\""));
        Assert.Equal(state == "User", await app.ScalarAsync("SELECT \"IsActive\" FROM \"Users\""));
    }

    [Fact]
    public async Task Existing_player_is_preserved_without_role_state_or_password_changes()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        using (var scope = app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>(); var user = User.RegisterAdministrator("Existing", Email.Create(PlayerEmail), "existing-private-hash", DateTime.UtcNow);
            user.ChangeActiveStatus(false); db.Users.Add(user); await db.SaveChangesAsync();
        }
        EnableInMemoryOptions(app); using var execute = app.Services.CreateScope();
        await execute.ServiceProvider.GetRequiredService<AdminBootstrapService>().ExecuteAsync(CancellationToken.None);
        Assert.Equal("Administrator:False:existing-private-hash", await app.ScalarAsync("SELECT \"Role\" || ':' || initcap(\"IsActive\"::text) || ':' || \"PasswordHash\" FROM \"Users\" WHERE \"Email\"='bootstrap-player@example.com'"));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Six_concurrent_bootstraps_create_only_two_accounts_and_two_events()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); EnableInMemoryOptions(app);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 6).Select(async _ =>
        {
            await gate.Task; using var scope = app.Services.CreateScope();
            return await scope.ServiceProvider.GetRequiredService<AdminBootstrapService>().ExecuteAsync(CancellationToken.None);
        }).ToArray(); gate.SetResult(); var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Equal(2, results.Sum(x => x.CreatedAccounts.Count)); Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Commit_failure_rolls_back_both_seed_accounts_and_both_events()
    {
        var failure = new FailCommitOnce();
        await using var app = await fixture.CreateAsync(customize: services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(failure)));
        using var client = app.CreateClient(); EnableInMemoryOptions(app); failure.Armed = true;
        using (var scope = app.Services.CreateScope())
            await Assert.ThrowsAsync<InvalidOperationException>(() => scope.ServiceProvider.GetRequiredService<AdminBootstrapService>().ExecuteAsync(CancellationToken.None));
        Assert.True(failure.SawAllWrites); Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        using var retry = app.Services.CreateScope(); await retry.ServiceProvider.GetRequiredService<AdminBootstrapService>().ExecuteAsync(CancellationToken.None);
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
    }

    [Fact]
    public async Task External_registration_winning_email_race_is_preserved_and_bootstrap_batch_rolls_back()
    {
        var collision = new BeforeSaveOnce();
        await using var app = await fixture.CreateAsync(customize: services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(collision)));
        using var client = app.CreateClient(); EnableInMemoryOptions(app);
        collision.Action = async () =>
        {
            using var outside = app.Services.CreateScope(); var db = outside.ServiceProvider.GetRequiredService<UsersDbContext>();
            db.Users.Add(User.Register("External", Email.Create(AdminEmail), "external-private-hash", DateTime.UtcNow));
            await db.SaveChangesAsync();
        };
        using var execution = app.Services.CreateScope();
        await Assert.ThrowsAsync<AdminBootstrapConflictException>(() => execution.ServiceProvider.GetRequiredService<AdminBootstrapService>().ExecuteAsync(CancellationToken.None));
        Assert.Equal("User:external-private-hash", await app.ScalarAsync("SELECT \"Role\" || ':' || \"PasswordHash\" FROM \"Users\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Startup_failure_exposes_only_safe_type_and_never_original_exception_secrets()
    {
        await using var app = await fixture.CreateAsync(customize: services => services.AddScoped<IPasswordHasher, FailingHasher>());
        Configure(app); var error = Assert.ThrowsAny<Exception>(() => app.CreateClient());
        var diagnostic = error.ToString() + string.Join('\n', app.Logs.Entries);
        Assert.DoesNotContain(FailingHasher.Secret, diagnostic);
        Assert.Contains(nameof(AdminBootstrapStartupException), diagnostic);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    private sealed class FailingHasher : IPasswordHasher
    {
        public const string Secret = "password-in-original-exception-FixtureOnly!";
        public string Hash(string password) => throw new InvalidOperationException(Secret);
        public bool Verify(string? passwordHash, string password) => throw new InvalidOperationException(Secret);
    }
    private sealed class BeforeSaveOnce : SaveChangesInterceptor
    {
        public Func<Task>? Action { get; set; }
        public override async ValueTask<InterceptionResult<int>> SavingChangesAsync(DbContextEventData eventData,
            InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            var action = Action; Action = null; if (action is not null) await action(); return result;
        }
    }

    internal const string AdminEmail = "bootstrap-admin@example.com";
    internal const string AdminPassword = "Adm1n!Fixture2026";
    internal const string PlayerEmail = "bootstrap-player@example.com";
    internal const string PlayerPassword = "Play3r!Fixture2026";
    internal static void Configure(AuthFactory app)
    {
        app.Overrides["AdminBootstrap:Enabled"] = "true";
        app.Overrides["AdminBootstrap:Email"] = AdminEmail; app.Overrides["AdminBootstrap:Password"] = AdminPassword;
        app.Overrides["AdminBootstrap:PlayerEmail"] = PlayerEmail; app.Overrides["AdminBootstrap:PlayerPassword"] = PlayerPassword;
    }
    private static void EnableInMemoryOptions(AuthFactory app)
    {
        // Exercises the startup application in an already-migrated fresh host without running it once before the barrier/fault.
        var options = app.Services.GetRequiredService<IOptions<AdminBootstrapOptions>>().Value;
        options.Enabled = true; options.Email = AdminEmail; options.Password = AdminPassword; options.PlayerEmail = PlayerEmail; options.PlayerPassword = PlayerPassword;
    }
    private sealed class FailCommitOnce : DbTransactionInterceptor
    {
        public bool Armed { get; set; } public bool SawAllWrites { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction,
            TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result; await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT (SELECT count(*) FROM \"Users\")=2 AND (SELECT count(*) FROM \"OutboxMessage\")=2";
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!) return result;
            Armed = false; SawAllWrites = true; throw new InvalidOperationException("Synthetic bootstrap commit failure.");
        }
    }
}
