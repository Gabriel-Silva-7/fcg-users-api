using System.Data.Common;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using System.Net;
using System.Net.Http.Json;
using System.Net.Http.Headers;
using System.Text.Json;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class OwnProfileTests(AuthFixture fixture)
{
    [Fact]
    public async Task Registration_location_resolves_to_own_profile_after_real_login()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        var account = new { name = "Person", email = "profile@example.com", password = "ProfileFixture1!" };
        using var registered = await client.PostAsJsonAsync("/api/v1/auth/register", account); Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { account.email, account.password });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString());
        using var response = await client.GetAsync(registered.Headers.Location);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var profile = await response.Content.ReadFromJsonAsync<JsonElement>(); Assert.Equal(account.email, profile.GetProperty("email").GetString());
    }

    [Fact]
    public async Task Profile_is_owned_by_sub_normalizes_contact_preserves_credentials_and_releases_old_email()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); using var admin = await ClientAsync(app, true);
        var id = await IdAsync(app); var adminId = await IdAsync(app, BootstrapTests.AdminEmail);
        var hash = await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\" WHERE \"Id\"=@id", P(id));
        using var changed = await player.PatchAsJsonAsync(Me + "?userId=" + adminId,
            new { name = "  Updated Person  ", email = "  UPDATED-PROFILE@EXAMPLE.COM ", userId = adminId, role = "Administrator", isDeleted = true });
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode); var body = await JsonAsync(changed);
        Assert.Equal(new[] { "email", "id", "name", "role" }, body.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(id, body.GetProperty("id").GetGuid()); Assert.Equal("Updated Person", body.GetProperty("name").GetString());
        Assert.Equal("updated-profile@example.com", body.GetProperty("email").GetString()); Assert.Equal("User", body.GetProperty("role").GetString());
        using var own = await player.GetAsync(Me + "?id=" + adminId); Assert.Equal(id, (await JsonAsync(own)).GetProperty("id").GetGuid());
        using var adminOwn = await admin.GetAsync(Me + "?userId=" + id); var adminBody = await JsonAsync(adminOwn);
        Assert.Equal(adminId, adminBody.GetProperty("id").GetGuid()); Assert.Equal("Administrator", adminBody.GetProperty("role").GetString());
        Assert.Equal(hash, await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\" WHERE \"Id\"=@id", P(id)));
        Assert.Equal(2L, await StateVersionAsync(app, id)); var version = await VersionAsync(app, id);
        using var noop = await player.PatchAsJsonAsync(Me, new { name = " Updated Person ", email = "UPDATED-PROFILE@EXAMPLE.COM" });
        Assert.Equal(HttpStatusCode.OK, noop.StatusCode); Assert.Equal(2L, await StateVersionAsync(app, id)); Assert.NotEqual(version, await VersionAsync(app, id));
        using var freed = await admin.PostAsJsonAsync("/api/v1/auth/register", new { name = "New Owner", email = BootstrapTests.PlayerEmail, password = "NewOwnerFixture1!" });
        Assert.Equal(HttpStatusCode.Created, freed.StatusCode); Assert.NotEqual(id, (await JsonAsync(freed)).GetProperty("id").GetGuid());
        using var updatedLogin = await admin.PostAsJsonAsync("/api/v1/auth/login", new { email = "updated-profile@example.com", password = BootstrapTests.PlayerPassword }); Assert.Equal(HttpStatusCode.OK, updatedLogin.StatusCode);
        var logs = string.Join('\n', app.Logs.Entries); Assert.DoesNotContain("updated-profile@example.com", logs); Assert.DoesNotContain("NewOwnerFixture1!", logs);
        Assert.Equal(2, app.Logs.Entries.Count(x => x.Contains("UserProfileUpdated")));
    }

    [Theory]
    [InlineData("Active")] [InlineData("Blocked")] [InlineData("Deleted")]
    public async Task Reserved_email_conflicts_even_for_blocked_or_deleted_account_without_partial_edit(string state)
    {
        await using var app = await StartAsync(); using var admin = await ClientAsync(app, true); using var player = await ClientAsync(app);
        using var registered = await admin.PostAsJsonAsync("/api/v1/auth/register", new { name = "Reserved", email = "reserved@example.com", password = "ReservedFixture1!" });
        var reserved = (await JsonAsync(registered)).GetProperty("id").GetGuid();
        if (state == "Blocked") Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync(Admin + reserved + "/status", new { isActive = false, version = await VersionAsync(app, reserved) })).StatusCode);
        if (state == "Deleted") Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Admin + reserved)).StatusCode);
        var id = await IdAsync(app); var before = await SnapshotAsync(app, id);
        await ErrorAsync(await player.PatchAsJsonAsync(Me, new { name = "Should not persist", email = " RESERVED@EXAMPLE.COM " }), HttpStatusCode.Conflict, "email_already_registered");
        Assert.Equal(before, await SnapshotAsync(app, id)); Assert.DoesNotContain(app.Logs.Entries, x => x.Contains("UserProfileUpdated"));
    }

    [Theory]
    [InlineData(null, "valid@example.com")] [InlineData("", "valid@example.com")] [InlineData("   ", "valid@example.com")]
    [InlineData("Valid", null)] [InlineData("Valid", "")] [InlineData("Valid", "invalid")]
    public async Task Invalid_profile_is_400_without_changes(string? name, string? email)
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); var id = await IdAsync(app); var before = await SnapshotAsync(app, id);
        await ErrorAsync(await player.PatchAsJsonAsync(Me, new { name, email }), HttpStatusCode.BadRequest, "validation_error");
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Fact]
    public async Task Profile_limits_are_enforced_and_name_boundary_is_preserved()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app);
        await ErrorAsync(await player.PatchAsJsonAsync(Me, new { name = new string('n',121), email = "boundary@example.com" }), HttpStatusCode.BadRequest, "validation_error");
        await ErrorAsync(await player.PatchAsJsonAsync(Me, new { name = "Valid", email = new string('x',257) }), HttpStatusCode.BadRequest, "validation_error");
        using var boundary = await player.PatchAsJsonAsync(Me, new { name = new string('n',120), email = "boundary@example.com" }); Assert.Equal(HttpStatusCode.OK, boundary.StatusCode);
        Assert.Equal(120, (await JsonAsync(boundary)).GetProperty("name").GetString()!.Length);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Profile_loses_to_delete_or_block_after_read_without_changing_snapshot(bool delete)
    {
        var barrier = new ProfileLoadBarrier(1);
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var player = await ClientAsync(app); using var admin = await ClientAsync(app, true); var id = await IdAsync(app); var version = await VersionAsync(app, id);
        barrier.Armed = true; var editing = player.PatchAsJsonAsync(Me, new { name = "Attempted Contact", email = "attempted@example.com" });
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var changed = delete ? await admin.DeleteAsync(Admin + id) : await admin.PatchAsJsonAsync(Admin + id + "/status", new { isActive = false, version });
            Assert.Equal(delete ? HttpStatusCode.NoContent : HttpStatusCode.OK, changed.StatusCode);
            var confirmed = await SnapshotAsync(app, id); barrier.Release.TrySetResult();
            await ErrorAsync(await editing, HttpStatusCode.Unauthorized, "unauthenticated");
            Assert.Equal(confirmed, await SnapshotAsync(app, id)); Assert.Equal(2L, await StateVersionAsync(app, id));
            Assert.DoesNotContain(app.Logs.Entries, x => x.Contains("UserProfileUpdated"));
            Assert.Equal(HttpStatusCode.Unauthorized, (await player.GetAsync(Me)).StatusCode);
        }
        finally { barrier.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Profile_winning_before_delete_remains_in_deleted_history()
    {
        var barrier = new ProfileCommitBarrier();
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var player = await ClientAsync(app); using var admin = await ClientAsync(app, true); var id = await IdAsync(app); barrier.Armed = true;
        var editing = player.PatchAsJsonAsync(Me, new { name = "WinnerProfile", email = "winner@example.com" });
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var deleting = admin.DeleteAsync(Admin + id); var deadline = DateTime.UtcNow.AddSeconds(2);
            while ((long)(await app.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%FOR UPDATE%'"))! == 0)
            { Assert.True(DateTime.UtcNow < deadline, "Delete must actually wait for the profile row lock"); await Task.Delay(10); }
            barrier.Release.TrySetResult(); Assert.Equal(HttpStatusCode.OK, (await editing).StatusCode); Assert.Equal(HttpStatusCode.NoContent, (await deleting).StatusCode);
            Assert.Equal("WinnerProfile:winner@example.com:true:false", await app.ScalarAsync("SELECT \"Name\" || ':' || \"Email\" || ':' || \"IsDeleted\"::text || ':' || \"IsActive\"::text FROM \"Users\" WHERE \"Id\"=@id", P(id)));
            Assert.Equal(3L, await StateVersionAsync(app, id));
        }
        finally { barrier.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Two_profile_edits_of_same_snapshot_have_one_winner_and_one_conflict()
    {
        var barrier = new ProfileLoadBarrier(2);
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var player = await ClientAsync(app); var id = await IdAsync(app); barrier.Armed = true;
        var edits = new[] { player.PatchAsJsonAsync(Me, new { name = "First", email = "first@example.com" }), player.PatchAsJsonAsync(Me, new { name = "Second", email = "second@example.com" }) };
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); barrier.Release.TrySetResult();
        var responses = await Task.WhenAll(edits); var winner = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict); Assert.Equal("concurrency_conflict", (await JsonAsync(loser)).GetProperty("code").GetString());
        var body = await JsonAsync(winner); Assert.Equal(body.GetProperty("email").GetString(), await app.ScalarAsync("SELECT \"Email\" FROM \"Users\" WHERE \"Id\"=@id", P(id)));
        Assert.Equal(2L, await StateVersionAsync(app, id)); foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Different_owners_competing_for_email_have_one_winner_and_one_unique_conflict()
    {
        var barrier = new ProfileLoadBarrier(2);
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var player = await ClientAsync(app); using var admin = await ClientAsync(app, true); barrier.Armed = true;
        var edits = new[] { player.PatchAsJsonAsync(Me, new { name = "PlayerWinner", email = "shared@example.com" }), admin.PatchAsJsonAsync(Me, new { name = "AdminWinner", email = "shared@example.com" }) };
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); barrier.Release.TrySetResult();
        var responses = await Task.WhenAll(edits); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK);
        var loser = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict); Assert.Equal("email_already_registered", (await JsonAsync(loser)).GetProperty("code").GetString());
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\" WHERE \"Email\"='shared@example.com'"));
        Assert.Equal(3m, await app.ScalarAsync("SELECT sum(\"StateVersion\") FROM \"Users\""));
        foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Profile_commit_failure_rolls_back_contact_and_version_without_success_log()
    {
        var fault = new FailProfileCommit();
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(fault)));
        using var player = await ClientAsync(app); var id = await IdAsync(app); var before = await SnapshotAsync(app, id); fault.Armed = true;
        await ErrorAsync(await player.PatchAsJsonAsync(Me, new { name = "RollbackProfile", email = "rollback@example.com" }), HttpStatusCode.InternalServerError, "internal_error");
        Assert.True(fault.SawWrite); Assert.Equal(before, await SnapshotAsync(app, id)); Assert.DoesNotContain(app.Logs.Entries, x => x.Contains("UserProfileUpdated"));
        Assert.DoesNotContain("fixture-profile-private-secret", string.Join('\n', app.Logs.Entries));
        Assert.Equal(HttpStatusCode.OK, (await player.PatchAsJsonAsync(Me, new { name = "Retry", email = "retry@example.com" })).StatusCode);
    }

    [Fact]
    public async Task Anonymous_invalid_token_and_state_outage_fail_before_profile_action()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); using var anonymous = app.CreateClient();
        await ErrorAsync(await anonymous.GetAsync(Me), HttpStatusCode.Unauthorized, "unauthenticated");
        await ErrorAsync(await anonymous.PatchAsJsonAsync(Me, new { name = "Valid", email = "valid@example.com" }), HttpStatusCode.Unauthorized, "unauthenticated");
        await fixture.PauseAsync();
        try
        {
            foreach (var patch in new[] { false, true })
            {
                using var response = patch ? await player.PatchAsJsonAsync(Me, new { name = "Valid", email = "valid@example.com" }) : await player.GetAsync(Me);
                Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal("1", response.Headers.RetryAfter?.ToString());
                Assert.Equal("auth_state_unavailable", (await JsonAsync(response)).GetProperty("code").GetString());
            }
            using var invalid = app.CreateClient(); invalid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await invalid.GetAsync(Me)).StatusCode);
        }
        finally { await fixture.UnpauseAsync(); }
    }

    private const string Me = "/api/v1/me";
    private const string Admin = "/api/v1/admin/users/";
    private async Task<AuthFactory> StartAsync(Action<IServiceCollection>? customize = null)
    { var app = await fixture.CreateAsync(customize: customize); BootstrapTests.Configure(app); return app; }
    private static async Task<HttpClient> ClientAsync(AuthFactory app, bool admin = false)
    {
        var client = app.CreateClient(); using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new
        { email = admin ? BootstrapTests.AdminEmail : BootstrapTests.PlayerEmail, password = admin ? BootstrapTests.AdminPassword : BootstrapTests.PlayerPassword });
        login.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await JsonAsync(login)).GetProperty("accessToken").GetString()); return client;
    }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    { using (response) { Assert.Equal(status, response.StatusCode); Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString()); } }
    private static NpgsqlParameter P(Guid id) => new("id", id);
    private static async Task<Guid> IdAsync(AuthFactory app, string email = BootstrapTests.PlayerEmail) => (Guid)(await app.ScalarAsync("SELECT \"Id\" FROM \"Users\" WHERE \"Email\"=@email", new NpgsqlParameter("email", email)))!;
    private static async Task<string> VersionAsync(AuthFactory app, Guid id) => (string)(await app.ScalarAsync("SELECT xmin::text FROM \"Users\" WHERE \"Id\"=@id", P(id)))!;
    private static async Task<long> StateVersionAsync(AuthFactory app, Guid id) => (long)(await app.ScalarAsync("SELECT \"StateVersion\" FROM \"Users\" WHERE \"Id\"=@id", P(id)))!;
    private static async Task<string> SnapshotAsync(AuthFactory app, Guid id) => (string)(await app.ScalarAsync("SELECT row_to_json(t)::text FROM (SELECT *, xmin::text AS token FROM \"Users\" WHERE \"Id\"=@id) t", P(id)))!;

    private sealed class ProfileLoadBarrier(int readers) : DbCommandInterceptor
    {
        public bool Armed { get; set; } private int count;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("self-edit-load"))
            { if (Interlocked.Increment(ref count) == readers) Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }
    private sealed class ProfileCommitBarrier : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result; await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"Users\" WHERE \"Name\"='WinnerProfile'";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0) return result;
            Armed = false; Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); return result;
        }
    }
    private sealed class FailProfileCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; } public bool SawWrite { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result; await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"Users\" WHERE \"Name\"='RollbackProfile'";
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0) return result;
            Armed = false; SawWrite = true; throw new InvalidOperationException("fixture-profile-private-secret");
        }
    }
}
