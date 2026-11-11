using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Data.Common;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class AdminLifecycleTests(AuthFixture fixture)
{
    [Fact]
    public async Task Delete_is_logical_and_idempotent_without_releasing_email_or_history()
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); using var player = await ClientAsync(app, true);
        var id = await IdAsync(app); var before = await SnapshotAsync(app, id);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Path(id))).StatusCode);
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        var deleted = await SnapshotAsync(app, id); Assert.NotEqual(before, deleted);
        Assert.Equal(true, await app.ScalarAsync("SELECT \"IsDeleted\" AND NOT \"IsActive\" FROM \"Users\" WHERE \"Id\"=@id", P(id)));
        Assert.Equal(2L, await StateVersionAsync(app, id));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Path(id))).StatusCode);
        Assert.Equal(deleted, await SnapshotAsync(app, id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await player.GetAsync("/test-auth/user")).StatusCode);
        using var login = await admin.PostAsJsonAsync("/api/v1/auth/login", new { email = BootstrapTests.PlayerEmail, password = BootstrapTests.PlayerPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        using var duplicate = await admin.PostAsJsonAsync("/api/v1/auth/register", new { name = "Another", email = "  BOOTSTRAP-PLAYER@EXAMPLE.COM ", password = BootstrapTests.PlayerPassword });
        await ErrorAsync(duplicate, HttpStatusCode.Conflict, "email_already_registered");
        Assert.Equal(3L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\"")); // Two registrations and the one deletion transition.
        Assert.Single(app.Logs.Entries, x => x.Contains("UserDeleted"));
    }

    [Fact]
    public async Task Block_unblock_and_status_noop_preserve_opaque_concurrency_and_current_authorization()
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); using var player = await ClientAsync(app, true);
        var id = await IdAsync(app); var original = await VersionAsync(app, id);
        using var blocked = await PatchAsync(admin, id, false, original);
        Assert.Equal(HttpStatusCode.OK, blocked.StatusCode); var body = await JsonAsync(blocked); var blockedVersion = body.GetProperty("version").GetString()!;
        Assert.Equal(new[] { "createdAt", "email", "id", "isActive", "isDeleted", "name", "role", "version" }, body.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.False(body.GetProperty("isActive").GetBoolean()); Assert.False(body.GetProperty("isDeleted").GetBoolean());
        Assert.NotEqual(original, blockedVersion); Assert.Equal(2L, await StateVersionAsync(app, id));
        Assert.Equal(HttpStatusCode.Unauthorized, (await player.GetAsync("/test-auth/user")).StatusCode);
        using var deniedLogin = await admin.PostAsJsonAsync("/api/v1/auth/login", new { email = BootstrapTests.PlayerEmail, password = BootstrapTests.PlayerPassword });
        Assert.Equal(HttpStatusCode.Unauthorized, deniedLogin.StatusCode);
        await ErrorAsync(await PatchAsync(admin, id, false, original), HttpStatusCode.Conflict, "concurrency_conflict");
        using var active = await PatchAsync(admin, id, true, blockedVersion); Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        var activeVersion = (await JsonAsync(active)).GetProperty("version").GetString()!;
        Assert.Equal(HttpStatusCode.OK, (await player.GetAsync("/test-auth/user")).StatusCode); Assert.Equal(3L, await StateVersionAsync(app, id));
        using var noop = await PatchAsync(admin, id, true, activeVersion); Assert.Equal(HttpStatusCode.OK, noop.StatusCode);
        Assert.NotEqual(activeVersion, (await JsonAsync(noop)).GetProperty("version").GetString());
        Assert.Equal(3L, await StateVersionAsync(app, id));
        await ErrorAsync(await PatchAsync(admin, id, true, activeVersion), HttpStatusCode.Conflict, "concurrency_conflict");
    }

    [Fact]
    public async Task Deleted_account_rejects_stale_version_before_terminal_state_and_never_restores()
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); var id = await IdAsync(app); var old = await VersionAsync(app, id);
        Assert.Equal(HttpStatusCode.OK, (await PatchAsync(admin, id, false, old)).StatusCode); // A blocked account can also be deleted.
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Path(id))).StatusCode); var snapshot = await SnapshotAsync(app, id);
        await ErrorAsync(await PatchAsync(admin, id, true, old), HttpStatusCode.Conflict, "concurrency_conflict");
        var current = await VersionAsync(app, id);
        await ErrorAsync(await PatchAsync(admin, id, true, current), HttpStatusCode.Conflict, "user_deleted");
        await ErrorAsync(await PatchAsync(admin, id, false, current), HttpStatusCode.Conflict, "user_deleted");
        Assert.Equal(snapshot, await SnapshotAsync(app, id)); Assert.Equal(3L, await StateVersionAsync(app, id));
    }

    [Fact]
    public async Task Self_protection_not_found_and_syntax_precedence_match_contract()
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); var own = await IdAsync(app, BootstrapTests.AdminEmail);
        await ErrorAsync(await admin.DeleteAsync(Path(own)), HttpStatusCode.Conflict, "cannot_delete_self");
        await ErrorAsync(await PatchAsync(admin, own, false, "not-a-version"), HttpStatusCode.BadRequest, "validation_error");
        await ErrorAsync(await PatchAsync(admin, own, false, "0"), HttpStatusCode.Conflict, "cannot_deactivate_self");
        await ErrorAsync(await admin.DeleteAsync(Path(Guid.NewGuid())), HttpStatusCode.NotFound, "resource_not_found");
        await ErrorAsync(await PatchAsync(admin, Guid.NewGuid(), true, "0"), HttpStatusCode.NotFound, "resource_not_found");
        Assert.Equal(1L, await StateVersionAsync(app, own));
    }

    [Theory]
    [InlineData(null)] [InlineData("")] [InlineData("-1")] [InlineData("+1")] [InlineData(" 1 ")]
    [InlineData("1.0")] [InlineData("4294967296")] [InlineData("12345678901")]
    public async Task Invalid_status_version_is_validation_error(string? version)
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); var id = await IdAsync(app);
        await ErrorAsync(await PatchAsync(admin, id, false, version), HttpStatusCode.BadRequest, "validation_error");
        Assert.Equal(1L, await StateVersionAsync(app, id));
    }

    [Fact]
    public async Task Authentication_and_current_administrator_role_precede_target_access()
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); using var player = await ClientAsync(app, true); using var anonymous = app.CreateClient();
        var id = await IdAsync(app);
        await ErrorAsync(await anonymous.DeleteAsync(Path(id)), HttpStatusCode.Unauthorized, "unauthenticated");
        await ErrorAsync(await PatchAsync(anonymous, id, false, "bad"), HttpStatusCode.Unauthorized, "unauthenticated");
        await ErrorAsync(await player.DeleteAsync(Path(id)), HttpStatusCode.Forbidden, "forbidden");
        await ErrorAsync(await PatchAsync(player, id, true, "0"), HttpStatusCode.Forbidden, "forbidden");
        await app.ExecuteAsync("UPDATE \"Users\" SET \"Role\"='User' WHERE \"Email\"='bootstrap-admin@example.com'");
        await ErrorAsync(await admin.DeleteAsync(Path(id)), HttpStatusCode.Forbidden, "forbidden");
        Assert.Equal(false, await app.ScalarAsync("SELECT \"IsDeleted\" FROM \"Users\" WHERE \"Id\"=@id", P(id)));
    }

    [Fact]
    public async Task Six_concurrent_deletes_have_one_transition_and_one_log()
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); var id = await IdAsync(app);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 6).Select(async _ => { await gate.Task; return await admin.DeleteAsync(Path(id)); }).ToArray(); gate.SetResult();
        var responses = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.All(responses, response => Assert.Equal(HttpStatusCode.NoContent, response.StatusCode));
        foreach (var response in responses) response.Dispose();
        Assert.Equal(2L, await StateVersionAsync(app, id)); Assert.Single(app.Logs.Entries, x => x.Contains("UserDeleted"));
    }

    [Fact]
    public async Task Concurrent_status_commands_with_same_token_have_one_winner()
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); var id = await IdAsync(app); var version = await VersionAsync(app, id);
        var gate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 6).Select(async _ => { await gate.Task; return await PatchAsync(admin, id, false, version); }).ToArray(); gate.SetResult();
        var responses = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        Assert.Equal(5, responses.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        foreach (var response in responses.Where(x => x.StatusCode == HttpStatusCode.Conflict)) Assert.Equal("concurrency_conflict", (await JsonAsync(response)).GetProperty("code").GetString());
        foreach (var response in responses) response.Dispose();
        Assert.Equal(2L, await StateVersionAsync(app, id));
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Row_lock_orders_delete_and_patch_in_both_commit_orders(bool deleteFirst)
    {
        var barrier = new LockedRowBarrier();
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var admin = await ClientAsync(app); var id = await IdAsync(app); var version = await VersionAsync(app, id); barrier.Armed = true;
        var first = deleteFirst ? admin.DeleteAsync(Path(id)) : PatchAsync(admin, id, false, version);
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var second = deleteFirst ? PatchAsync(admin, id, true, version) : admin.DeleteAsync(Path(id));
            var deadline = DateTime.UtcNow.AddSeconds(2);
            while ((long)(await app.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%FOR UPDATE%'"))! == 0)
            { Assert.True(DateTime.UtcNow < deadline, "Second request must visibly wait for the row lock"); await Task.Delay(10); }
            barrier.Release.TrySetResult();
            using var firstResult = await first; using var secondResult = await second;
            Assert.Equal(deleteFirst ? HttpStatusCode.NoContent : HttpStatusCode.OK, firstResult.StatusCode);
            Assert.Equal(deleteFirst ? HttpStatusCode.Conflict : HttpStatusCode.NoContent, secondResult.StatusCode);
            if (deleteFirst) Assert.Equal("concurrency_conflict", (await JsonAsync(secondResult)).GetProperty("code").GetString());
            Assert.Equal(deleteFirst ? 2L : 3L, await StateVersionAsync(app, id));
            Assert.Equal(true, await app.ScalarAsync("SELECT \"IsDeleted\" AND NOT \"IsActive\" FROM \"Users\" WHERE \"Id\"=@id", P(id)));
        }
        finally { barrier.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Delete_commit_failure_does_not_confirm_state_or_log()
    {
        var fault = new FailDeleteCommit();
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(fault)));
        using var admin = await ClientAsync(app); var id = await IdAsync(app); var snapshot = await SnapshotAsync(app, id); fault.Armed = true;
        await ErrorAsync(await admin.DeleteAsync(Path(id)), HttpStatusCode.InternalServerError, "internal_error");
        Assert.True(fault.SawDeleted); Assert.Equal(snapshot, await SnapshotAsync(app, id));
        Assert.DoesNotContain(app.Logs.Entries, x => x.Contains("UserDeleted"));
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Path(id))).StatusCode);
        Assert.Equal(2L, await StateVersionAsync(app, id));
        var logs = string.Join('\n', app.Logs.Entries);
        foreach (var secret in new[] { BootstrapTests.AdminEmail, BootstrapTests.PlayerEmail, BootstrapTests.AdminPassword, BootstrapTests.PlayerPassword, "synthetic-private-secret" }) Assert.DoesNotContain(secret, logs);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task State_outage_is_503_before_admin_mutation_and_invalid_token_remains_401(bool delete)
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); var id = await IdAsync(app);
        var snapshot = await SnapshotAsync(app, id); await fixture.PauseAsync();
        try
        {
            using var response = delete ? await admin.DeleteAsync(Path(id)) : await PatchAsync(admin, id, false, "0");
            Assert.Equal("1", response.Headers.RetryAfter?.ToString());
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("auth_state_unavailable", (await JsonAsync(response)).GetProperty("code").GetString());
            using var invalid = app.CreateClient(); invalid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
            await ErrorAsync(await invalid.DeleteAsync(Path(id)), HttpStatusCode.Unauthorized, "unauthenticated");
        }
        finally { await fixture.UnpauseAsync(); }
        Assert.Equal(snapshot, await SnapshotAsync(app, id));
    }

    [Fact]
    public async Task Binding_requires_status_and_ignores_extra_privilege_fields_without_promoting()
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app); var id = await IdAsync(app); var version = await VersionAsync(app, id);
        await ErrorAsync(await admin.PatchAsJsonAsync(Path(id) + "/status", new { version }), HttpStatusCode.BadRequest, "validation_error");
        using var response = await admin.PatchAsJsonAsync(Path(id) + "/status", new { version, isActive = true, role = "Administrator", isDeleted = true, userId = Guid.NewGuid() });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode); var body = await JsonAsync(response);
        Assert.Equal("User", body.GetProperty("role").GetString()); Assert.False(body.GetProperty("isDeleted").GetBoolean());
        Assert.Equal(id, body.GetProperty("id").GetGuid()); Assert.Equal(1L, await StateVersionAsync(app, id));
    }

    private async Task<AuthFactory> StartAsync(Action<IServiceCollection>? customize = null)
    { var app = await fixture.CreateAsync(customize: customize); BootstrapTests.Configure(app); return app; }
    private static async Task<HttpClient> ClientAsync(AuthFactory app, bool player = false)
    {
        var client = app.CreateClient(); using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        { email = player ? BootstrapTests.PlayerEmail : BootstrapTests.AdminEmail, password = player ? BootstrapTests.PlayerPassword : BootstrapTests.AdminPassword });
        login.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await JsonAsync(login)).GetProperty("accessToken").GetString()); return client;
    }
    private static Task<HttpResponseMessage> PatchAsync(HttpClient client, Guid id, bool isActive, string? version) =>
        client.PatchAsJsonAsync(Path(id) + "/status", new { isActive, version });
    private static string Path(Guid id) => "/api/v1/admin/users/" + id;
    private static NpgsqlParameter P(Guid id) => new("id", id);
    private static async Task<Guid> IdAsync(AuthFactory app, string email = BootstrapTests.PlayerEmail) => (Guid)(await app.ScalarAsync("SELECT \"Id\" FROM \"Users\" WHERE \"Email\"=@email", new NpgsqlParameter("email", email)))!;
    private static async Task<string> VersionAsync(AuthFactory app, Guid id) => (string)(await app.ScalarAsync("SELECT xmin::text FROM \"Users\" WHERE \"Id\"=@id", P(id)))!;
    private static async Task<long> StateVersionAsync(AuthFactory app, Guid id) => (long)(await app.ScalarAsync("SELECT \"StateVersion\" FROM \"Users\" WHERE \"Id\"=@id", P(id)))!;
    private static async Task<string> SnapshotAsync(AuthFactory app, Guid id) => (string)(await app.ScalarAsync("SELECT row_to_json(t)::text FROM (SELECT *, xmin::text AS token FROM \"Users\" WHERE \"Id\"=@id) t", P(id)))!;
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    { using (response) { Assert.Equal(status, response.StatusCode); Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType); Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString()); } }

    private sealed class LockedRowBarrier : DbCommandInterceptor
    {
        public bool Armed { get; set; } private int entered;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("FOR UPDATE") && Interlocked.CompareExchange(ref entered, 1, 0) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }
    private sealed class FailDeleteCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; } public bool SawDeleted { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result; await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"Users\" WHERE \"IsDeleted\"";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0) return result;
            Armed = false; SawDeleted = true; throw new InvalidOperationException("synthetic-private-secret");
        }
    }
}
