using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Users.Infrastructure.Identity;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class IdentityReconciliationTests(AuthFixture fixture)
{
    [Fact]
    public async Task Multiple_batches_repeat_without_mutating_users_or_fabricating_registration()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); await SeedAsync(app, 205);
        var before = await FingerprintAsync(app);
        using var scope = app.Services.CreateScope(); var reconciler = scope.ServiceProvider.GetRequiredService<IdentityReconciler>();
        var dry = await reconciler.ExecuteAsync(null, true, CancellationToken.None); Assert.Equal(205, dry.Selected); Assert.Equal(0, dry.Staged); Assert.Equal(0L, await CountAsync(app));
        var first = await reconciler.ExecuteAsync(null, false, CancellationToken.None); Assert.Equal(205, first.Staged); Assert.Equal(68, first.Active); Assert.Equal(69, first.Blocked); Assert.Equal(68, first.Deleted);
        var second = await reconciler.ExecuteAsync(null, false, CancellationToken.None); Assert.Equal(205, second.Staged); Assert.Equal(410L, await CountAsync(app)); Assert.Equal(before, await FingerprintAsync(app));
        Assert.Equal(410L, await app.ScalarAsync("SELECT count(DISTINCT \"Body\"::jsonb->'message'->>'eventId') FROM \"OutboxMessage\""));
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\" WHERE position('urn:message:FCG.Contracts:UserCreatedEvent' in \"Body\")>0"));
        Assert.Equal(410L, await app.ScalarAsync("""
            SELECT count(*) FROM "OutboxMessage" m JOIN "Users" u ON (m."Body"::jsonb->'message'->>'userId')::uuid=u."Id"
            WHERE position('urn:message:FCG.Contracts:UserStateSnapshotEvent' in m."Body")>0
            AND (m."Body"::jsonb->'message'->>'eventVersion')::bigint=u."StateVersion"
            AND m."Body"::jsonb->'message'->>'name'=u."Name" AND m."Body"::jsonb->'message'->>'email'=u."Email"
            AND m."Body"::jsonb->'message'->>'role'=u."Role"
            AND m."Body"::jsonb->'message'->>'state'=CASE WHEN u."IsDeleted" THEN 'Deleted' WHEN u."IsActive" THEN 'Active' ELSE 'Blocked' END
            AND (m."Body"::jsonb->'message'->>'createdAtUtc')::timestamptz=u."CreatedAtUtc"
            """));
    }

    [Fact]
    public async Task Invalid_payload_in_later_batch_rolls_back_every_staged_snapshot()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); await SeedAsync(app, 105);
        await app.ExecuteAsync("UPDATE \"Users\" SET \"Name\"=' ' WHERE \"Id\"=lpad(to_hex(105),32,'0')::uuid"); var before = await FingerprintAsync(app);
        using var scope = app.Services.CreateScope(); var reconciler = scope.ServiceProvider.GetRequiredService<IdentityReconciler>();
        await Assert.ThrowsAsync<ArgumentException>(() => reconciler.ExecuteAsync(null, false, CancellationToken.None));
        Assert.Equal(0L, await CountAsync(app)); Assert.Equal(before, await FingerprintAsync(app));
        await app.ExecuteAsync("UPDATE \"Users\" SET \"Name\"='Valid repaired fixture' WHERE \"Id\"=lpad(to_hex(105),32,'0')::uuid");
        Assert.Equal(105, (await reconciler.ExecuteAsync(null, false, CancellationToken.None)).Staged); Assert.Equal(105L, await CountAsync(app));
    }

    [Fact]
    public async Task Individual_selection_and_missing_id_leave_every_account_unchanged()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); await SeedAsync(app, 3); var before = await FingerprintAsync(app);
        using var scope = app.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<IdentityReconciler>();
        var selected = Guid.Parse("00000000-0000-0000-0000-000000000002"); var report = await service.ExecuteAsync(selected, false, CancellationToken.None);
        Assert.Equal(1, report.Selected); Assert.Equal(1, report.Deleted); Assert.False(report.NotFound);
        Assert.Equal(selected, await app.ScalarAsync("SELECT \"CorrelationId\" FROM \"OutboxMessage\""));
        var missing = await service.ExecuteAsync(Guid.NewGuid(), false, CancellationToken.None); Assert.True(missing.NotFound); Assert.Equal(0, missing.Staged);
        Assert.Equal(1L, await CountAsync(app)); Assert.Equal(before, await FingerprintAsync(app));
    }

    [Fact]
    public async Task Concurrent_new_state_can_commit_while_recovery_keeps_its_original_consistent_version()
    {
        var barrier = new RecoveryPageBarrier();
        await using var app = await fixture.CreateAsync(customize: services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        BootstrapTests.Configure(app); using var client = app.CreateClient(); using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = BootstrapTests.PlayerEmail, password = BootstrapTests.PlayerPassword });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        var id = (Guid)(await app.ScalarAsync("SELECT \"Id\" FROM \"Users\" WHERE \"Email\"='bootstrap-player@example.com'"))!;
        using var scope = app.Services.CreateScope(); var service = scope.ServiceProvider.GetRequiredService<IdentityReconciler>(); barrier.Armed = true;
        var recovery = service.ExecuteAsync(null, false, CancellationToken.None); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var update = await client.PatchAsJsonAsync("/api/v1/me", new { name = "Newer contact", email = "newer@example.com" }); Assert.Equal(HttpStatusCode.OK, update.StatusCode); barrier.Release.TrySetResult();
            Assert.Equal(2, (await recovery).Staged);
            Assert.Equal("1:Player:bootstrap-player@example.com", await app.ScalarAsync("SELECT (\"Body\"::jsonb->'message'->>'eventVersion') || ':' || (\"Body\"::jsonb->'message'->>'name') || ':' || (\"Body\"::jsonb->'message'->>'email') FROM \"OutboxMessage\" WHERE position('urn:message:FCG.Contracts:UserStateSnapshotEvent' in \"Body\")>0 AND \"CorrelationId\"=@id", new NpgsqlParameter("id", id)));
            Assert.Equal("2:Newer contact:newer@example.com", await app.ScalarAsync("SELECT (\"Body\"::jsonb->'message'->>'eventVersion') || ':' || (\"Body\"::jsonb->'message'->>'name') || ':' || (\"Body\"::jsonb->'message'->>'email') FROM \"OutboxMessage\" WHERE position('urn:message:FCG.Contracts:UserStateChangedEvent' in \"Body\")>0 AND \"CorrelationId\"=@id", new NpgsqlParameter("id", id)));
        }
        finally { barrier.Release.TrySetResult(); }
    }

    private static async Task<long> CountAsync(AuthFactory app) => (long)(await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""))!;
    private static async Task<object?> FingerprintAsync(AuthFactory app) => await app.ScalarAsync("SELECT md5(string_agg(row_to_json(t)::text,',' ORDER BY t.\"Id\")) FROM (SELECT *,xmin::text token FROM \"Users\")t");
    private static Task SeedAsync(AuthFactory app, int count) => app.ExecuteAsync("""
        INSERT INTO "Users" ("Id","Name","Email","PasswordHash","Role","IsActive","IsDeleted","StateVersion","CreatedAtUtc")
        SELECT lpad(to_hex(i),32,'0')::uuid,'Recovery '||i,'recovery.'||i||'@example.com','fixture-private-hash',
        CASE WHEN i%2=0 THEN 'Administrator' ELSE 'User' END,i%3=0,i%3=2,CASE WHEN i%3=0 THEN 1 ELSE 2 END,TIMESTAMPTZ '2020-01-01T00:00:00Z'
        FROM generate_series(1,@count)i
        """, new NpgsqlParameter("count", count));
    private sealed class RecoveryPageBarrier : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        private int entered; public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("identity-reconciliation-page") && Interlocked.CompareExchange(ref entered, 1, 0) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }
}
