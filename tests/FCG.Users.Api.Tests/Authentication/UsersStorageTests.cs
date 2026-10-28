using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Json;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Identity;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Npgsql;
using Xunit;
using static FCG.Users.Api.Tests.Authentication.RegistrationTests;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class UsersStorageTests(AuthFixture fixture)
{
    [Fact]
    public async Task Concurrent_registrations_that_all_read_absence_have_one_winner_and_canonical_conflicts()
    {
        const int writers = 6;
        var barrier = new AbsentEmailBarrier(writers);
        await using var app = await fixture.CreateAsync(customize: services =>
        {
            services.RemoveAll<IUserRepository>();
            services.AddScoped<IUserRepository>(provider =>
            {
                var db = provider.GetRequiredService<UsersDbContext>();
                return new BarrierRepository(new UserRepository(db), barrier, db.ContextId.InstanceId);
            });
        });
        using var client = app.CreateClient();
        var attempts = Enumerable.Range(0, writers)
            .Select(index => RegisterAsync(client, index % 2 == 0 ? "  RACE@EXAMPLE.COM  " : "race@example.com"))
            .ToArray();
        var responses = await Task.WhenAll(attempts).WaitAsync(TimeSpan.FromSeconds(30));
        try
        {
            Assert.Equal(writers, barrier.Arrivals);
            Assert.Equal(writers, barrier.DistinctContexts);
            Assert.Equal(writers, barrier.AbsentReads);
            Assert.Single(responses, response => response.StatusCode == HttpStatusCode.Created);
            Assert.Equal(writers - 1, responses.Count(response => response.StatusCode == HttpStatusCode.Conflict));
            foreach (var response in responses.Where(response => response.StatusCode == HttpStatusCode.Conflict))
                await AssertProblemAsync(response, HttpStatusCode.Conflict, "email_already_registered");
            Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
            Assert.Equal("race@example.com", await app.ScalarAsync("SELECT \"Email\" FROM \"Users\""));
            Assert.Equal("User", await app.ScalarAsync("SELECT \"Role\" FROM \"Users\""));
            Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
            Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxState\""));
            var logs = string.Join('\n', app.Logs.Entries);
            Assert.DoesNotContain("race@example.com", logs, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(Password, logs);
            Assert.DoesNotContain("   at ", logs);

        }
        finally
        {
            foreach (var response in responses) response.Dispose();
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Blocked_and_deleted_accounts_keep_the_normalized_email_reserved(bool deleted)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var original = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, original.StatusCode);
        var hash = await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\"");
        await app.ExecuteAsync("UPDATE \"Users\" SET \"IsActive\" = false, \"IsDeleted\" = @deleted",
            new NpgsqlParameter("deleted", deleted));
        using var duplicate = await RegisterAsync(client, "  PERSON@EXAMPLE.COM  ");
        await AssertProblemAsync(duplicate, HttpStatusCode.Conflict, "email_already_registered");
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        Assert.Equal(false, await app.ScalarAsync("SELECT \"IsActive\" FROM \"Users\""));
        Assert.Equal(deleted, await app.ScalarAsync("SELECT \"IsDeleted\" FROM \"Users\""));
        Assert.Equal(hash, await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\""));
    }

    [Theory]
    [InlineData("name")]
    [InlineData("email")]
    [InlineData("password")]
    public async Task Registration_rejects_HTTP_lengths_above_the_preserved_limits_without_writing(string field)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync(RegistrationTests.Endpoint, new
        {
            name = field == "name" ? new string('n', 121) : "Name",
            email = field == "email" ? EmailAtMaximumLength() + "x" : "person@example.com",
            password = field == "password" ? "A1!" + new string('x', 126) : Password
        });
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
    }

    [Fact]
    public async Task Registration_and_login_accept_exact_maximum_lengths_and_preserve_hash_verification()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        var name = new string('n', 120);
        var email = EmailAtMaximumLength();
        var password = "A1!" + new string('x', 125);
        Assert.Equal(256, email.Length);
        Assert.Equal(128, password.Length);
        using var response = await client.PostAsJsonAsync(RegistrationTests.Endpoint, new { name, email, password });
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        using var login = await client.PostAsJsonAsync(LoginTests.Endpoint, new { email, password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Equal(name, await app.ScalarAsync("SELECT \"Name\" FROM \"Users\""));
        Assert.Equal(email, await app.ScalarAsync("SELECT \"Email\" FROM \"Users\""));
        Assert.NotEqual(password, await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\""));
    }

    [Theory]
    [InlineData("email")]
    [InlineData("password")]
    public async Task Login_rejects_overlong_fields_as_400_instead_of_invalid_credentials(string field)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync(LoginTests.Endpoint, new
        {
            email = field == "email" ? EmailAtMaximumLength() + "x" : "person@example.com",
            password = field == "password" ? "A1!" + new string('x', 126) : Password
        });
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_error");
    }

    [Theory]
    [InlineData("person@localhost")]
    [InlineData("person@[127.0.0.1]")]
    public async Task Registration_preserves_legacy_MailAddress_formats_and_the_fixed_identity_contract(string email)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var response = await RegisterAsync(client, email);
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal(Email.Create(email).Value, await app.ScalarAsync("SELECT \"Email\" FROM \"Users\""));
        using var login = await client.PostAsJsonAsync(LoginTests.Endpoint, new { email, password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Migration_is_idempotent_with_own_unprivileged_role_and_preserves_schema_and_xmin_semantics()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var registered = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var identity = new NpgsqlConnectionStringBuilder(app.ConnectionString);
        Assert.StartsWith("role_usr_", identity.Username);
        Assert.Equal(identity.Username, await app.ScalarAsync("SELECT current_user::text"));
        Assert.Equal(false, await app.ScalarAsync("SELECT rolsuper OR rolcreatedb OR rolcreaterole FROM pg_roles WHERE rolname = current_user"));
        Assert.Equal(true, await app.ScalarAsync("SELECT pg_get_userbyid(datdba) = current_user FROM pg_database WHERE datname = current_database()"));
        Assert.Equal(1L, await app.ScalarAsync("SELECT \"StateVersion\" FROM \"Users\""));
        Assert.Equal(false, await app.ScalarAsync("SELECT \"IsDeleted\" FROM \"Users\""));
        var originalXmin = Assert.IsType<long>(await app.ScalarAsync("SELECT xmin::text::bigint FROM \"Users\""));
        Assert.True(originalXmin > 0);
        var originalHash = await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\"");
        await using (var scope = app.Services.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<UsersDbContext>();
            var before = (await db.Database.GetAppliedMigrationsAsync()).ToArray();
            Assert.NotEmpty(before);
            Assert.False(db.Database.HasPendingModelChanges());
            await db.Database.MigrateAsync();
            await db.Database.MigrateAsync();
            Assert.Equal(before, (await db.Database.GetAppliedMigrationsAsync()).ToArray());
            Assert.Empty(await db.Database.GetPendingMigrationsAsync());
        }
        Assert.Equal(originalXmin, await app.ScalarAsync("SELECT xmin::text::bigint FROM \"Users\""));
        Assert.Equal(originalHash, await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\""));
        var tables = Assert.IsType<string[]>(await app.ScalarAsync("SELECT array_agg(tablename::text) FROM pg_tables WHERE schemaname = 'public'"));
        Assert.Equal(new[] { "Users", "InboxState", "OutboxMessage", "OutboxState", "__EFMigrationsHistory" }.Order(StringComparer.Ordinal), tables.Order(StringComparer.Ordinal));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM pg_constraint WHERE conrelid = '\"Users\"'::regclass AND contype = 'f'"));
        Assert.Equal(true, await app.ScalarAsync("SELECT indisunique FROM pg_index WHERE indexrelid = '\"UX_Users_Email\"'::regclass"));
        await app.ExecuteAsync("UPDATE \"Users\" SET \"PasswordHash\" = \"PasswordHash\"");
        Assert.NotEqual(originalXmin, await app.ScalarAsync("SELECT xmin::text::bigint FROM \"Users\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT \"StateVersion\" FROM \"Users\""));
    }

    [Theory]
    [InlineData("UPDATE \"Users\" SET \"Role\" = 'Owner'")]
    [InlineData("UPDATE \"Users\" SET \"StateVersion\" = 0")]
    [InlineData("UPDATE \"Users\" SET \"IsDeleted\" = true, \"IsActive\" = true")]
    public async Task PostgreSQL_enforces_role_lifecycle_and_positive_logical_version(string invalidSql)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var registered = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var error = await Assert.ThrowsAsync<PostgresException>(() => app.ExecuteAsync(invalidSql));
        Assert.Equal(PostgresErrorCodes.CheckViolation, error.SqlState);
        Assert.Equal("User", await app.ScalarAsync("SELECT \"Role\" FROM \"Users\""));
        Assert.Equal(true, await app.ScalarAsync("SELECT \"IsActive\" FROM \"Users\""));
        Assert.Equal(false, await app.ScalarAsync("SELECT \"IsDeleted\" FROM \"Users\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT \"StateVersion\" FROM \"Users\""));
    }

    private static string EmailAtMaximumLength() => new string('a', 64) + "@" +
        string.Join('.', new string('b', 48), new string('c', 48), new string('d', 48), new string('e', 44));

    private sealed class AbsentEmailBarrier(int participants)
    {
        private readonly TaskCompletionSource<bool> _allRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly ConcurrentDictionary<Guid, byte> _contexts = new();
        private int _arrivals;
        private int _absentReads;
        public int Arrivals => Volatile.Read(ref _arrivals);
        public int AbsentReads => Volatile.Read(ref _absentReads);
        public int DistinctContexts => _contexts.Count;
        public async Task ArriveAsync(Guid contextId, bool exists, CancellationToken cancellationToken)
        {
            _contexts.TryAdd(contextId, 0);
            if (!exists) Interlocked.Increment(ref _absentReads);
            if (Interlocked.Increment(ref _arrivals) == participants) _allRead.TrySetResult(true);
            await _allRead.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken);
        }
    }

    private sealed class BarrierRepository(IUserRepository inner, AbsentEmailBarrier barrier, Guid contextId) : IUserRepository
    {
        public async Task<bool> ExistsByEmailAsync(Email email, CancellationToken cancellationToken)
        {
            var exists = await inner.ExistsByEmailAsync(email, cancellationToken);
            await barrier.ArriveAsync(contextId, exists, cancellationToken);
            return exists;
        }
        public Task<User?> FindByEmailAsync(Email email, CancellationToken cancellationToken) => inner.FindByEmailAsync(email, cancellationToken);
        public Task<User?> FindByIdAsync(Guid id, CancellationToken cancellationToken) => inner.FindByIdAsync(id, cancellationToken);
        public void Add(User user) => inner.Add(user);
        public Task SaveChangesAsync(CancellationToken cancellationToken) => inner.SaveChangesAsync(cancellationToken);
    }
}
