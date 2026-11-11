using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Users.Application.Identity;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class OwnPasswordTests(AuthFixture fixture)
{
    [Fact]
    public async Task Password_changes_with_current_credential_and_old_token_remains_valid()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); var id = await IdAsync(app);
        var token = player.DefaultRequestHeaders.Authorization!.Parameter!; var oldHash = await HashAsync(app, id); var version = await VersionAsync(app, id);
        using var previous = await player.GetAsync(Me); var profile = (await JsonAsync(previous)).GetRawText();
        const string next = "ChangedFixture1!";
        using var response = await ChangeAsync(player, BootstrapTests.PlayerPassword, next);
        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode); Assert.Empty(await response.Content.ReadAsStringAsync());
        Assert.NotEqual(oldHash, await HashAsync(app, id)); Assert.NotEqual(version, await VersionAsync(app, id)); Assert.Equal(1L, await StateVersionAsync(app, id));
        using var scope = app.Services.CreateScope(); var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        var hash = await HashAsync(app, id); Assert.True(hasher.Verify(hash, next)); Assert.False(hasher.Verify(hash, BootstrapTests.PlayerPassword));
        using var oldLogin = await player.PostAsJsonAsync(Login, new { email = BootstrapTests.PlayerEmail, password = BootstrapTests.PlayerPassword }); Assert.Equal(HttpStatusCode.Unauthorized, oldLogin.StatusCode);
        using var newLogin = await player.PostAsJsonAsync(Login, new { email = BootstrapTests.PlayerEmail, password = next }); Assert.Equal(HttpStatusCode.OK, newLogin.StatusCode);
        Assert.Equal(token, player.DefaultRequestHeaders.Authorization.Parameter);
        using var current = await player.GetAsync(Me); Assert.Equal(HttpStatusCode.OK, current.StatusCode); Assert.Equal(profile, (await JsonAsync(current)).GetRawText());
        Assert.Equal(2L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        var logs = string.Join('\n', app.Logs.Entries);
        foreach (var secret in new[] { BootstrapTests.PlayerPassword, next, oldHash, hash, token }) Assert.DoesNotContain(secret, logs);
        Assert.Single(app.Logs.Entries, x => x.Contains("UserPasswordChanged"));
    }

    [Fact]
    public async Task Password_operation_uses_sub_and_cannot_change_another_account_or_role()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); var id = await IdAsync(app); var adminId = await IdAsync(app, BootstrapTests.AdminEmail); var before = await SnapshotAsync(app, adminId);
        await ErrorAsync(await player.PatchAsJsonAsync(Password, new { userId = adminId, currentPassword = BootstrapTests.AdminPassword, newPassword = "ChangedFixture1!" }), HttpStatusCode.BadRequest, "invalid_current_password");
        using var changed = await player.PatchAsJsonAsync(Password + "?userId=" + adminId, new { userId = adminId, role = "Administrator", isActive = false, currentPassword = BootstrapTests.PlayerPassword, newPassword = "OwnChangedFixture1!" });
        Assert.Equal(HttpStatusCode.NoContent, changed.StatusCode); Assert.Equal(before, await SnapshotAsync(app, adminId));
        Assert.Equal("User", await app.ScalarAsync("SELECT \"Role\" FROM \"Users\" WHERE \"Id\"=@id", P(id)));
        using var adminLogin = await player.PostAsJsonAsync(Login, new { email = BootstrapTests.AdminEmail, password = BootstrapTests.AdminPassword }); Assert.Equal(HttpStatusCode.OK, adminLogin.StatusCode);
    }

    [Theory]
    [InlineData("WrongFixture1!")]
    [InlineData(" Play3r!Fixture2026")]
    [InlineData("play3r!fixture2026")]
    public async Task Incorrect_current_password_is_400_and_does_not_change_hash(string current)
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); var id = await IdAsync(app); var before = await SnapshotAsync(app, id);
        await ErrorAsync(await ChangeAsync(player, current, "ChangedFixture1!"), HttpStatusCode.BadRequest, "invalid_current_password");
        Assert.Equal(before, await SnapshotAsync(app, id)); Assert.DoesNotContain(app.Logs.Entries, x => x.Contains("UserPasswordChanged"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("abcdefgh")]
    [InlineData("12345678")]
    [InlineData("ABCdef12")]
    [InlineData("123456!@")]
    public async Task Missing_or_weak_new_password_is_validation_error(string? next)
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); var id = await IdAsync(app); var before = await SnapshotAsync(app, id);
        await ErrorAsync(await ChangeAsync(player, BootstrapTests.PlayerPassword, next), HttpStatusCode.BadRequest, "validation_error");
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Fact]
    public async Task Missing_and_too_long_current_or_new_passwords_are_validation_errors()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); var id = await IdAsync(app); var before = await SnapshotAsync(app, id);
        foreach (var current in new string?[] { null, "", " ", new string('x', 129) })
            await ErrorAsync(await ChangeAsync(player, current, "ChangedFixture1!"), HttpStatusCode.BadRequest, "validation_error");
        await ErrorAsync(await ChangeAsync(player, BootstrapTests.PlayerPassword, new string('a', 126) + "A1!"), HttpStatusCode.BadRequest, "validation_error");
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    [Theory]
    [InlineData(8)]
    [InlineData(128)]
    public async Task New_password_length_boundaries_are_accepted_with_real_hashing(int length)
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); var next = new string('a', length - 3) + "A1!";
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(player, BootstrapTests.PlayerPassword, next)).StatusCode);
        using var login = await player.PostAsJsonAsync(Login, new { email = BootstrapTests.PlayerEmail, password = next }); Assert.Equal(HttpStatusCode.OK, login.StatusCode);
    }

    [Fact]
    public async Task Password_whitespace_is_not_trimmed_and_same_password_is_permitted_with_fresh_hash()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); var id = await IdAsync(app); var before = await HashAsync(app, id);
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(player, BootstrapTests.PlayerPassword, BootstrapTests.PlayerPassword)).StatusCode);
        Assert.NotEqual(before, await HashAsync(app, id));
        const string spaced = "  NoTrimPassword1!  "; Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(player, BootstrapTests.PlayerPassword, spaced)).StatusCode);
        using var trimmed = await player.PostAsJsonAsync(Login, new { email = BootstrapTests.PlayerEmail, password = spaced.Trim() }); Assert.Equal(HttpStatusCode.Unauthorized, trimmed.StatusCode);
        using var exact = await player.PostAsJsonAsync(Login, new { email = BootstrapTests.PlayerEmail, password = spaced }); Assert.Equal(HttpStatusCode.OK, exact.StatusCode);
        await ErrorAsync(await ChangeAsync(player, spaced.Trim(), "AnotherFixture1!"), HttpStatusCode.BadRequest, "invalid_current_password");
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(player, spaced, "AnotherFixture1!")).StatusCode); Assert.Equal(1L, await StateVersionAsync(app, id));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Password_losing_to_delete_or_block_does_not_change_hash(bool delete)
    {
        var barrier = new SelfLoadBarrier(1);
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var player = await ClientAsync(app); using var admin = await ClientAsync(app, true); var id = await IdAsync(app); var version = await VersionAsync(app, id); barrier.Armed = true;
        var changing = ChangeAsync(player, BootstrapTests.PlayerPassword, "ChangedFixture1!"); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var access = delete ? await admin.DeleteAsync(Admin + id) : await admin.PatchAsJsonAsync(Admin + id + "/status", new { isActive = false, version });
            Assert.Equal(delete ? HttpStatusCode.NoContent : HttpStatusCode.OK, access.StatusCode); var confirmed = await SnapshotAsync(app, id); barrier.Release.TrySetResult();
            await ErrorAsync(await changing, HttpStatusCode.Unauthorized, "unauthenticated"); Assert.Equal(confirmed, await SnapshotAsync(app, id));
            Assert.DoesNotContain(app.Logs.Entries, x => x.Contains("UserPasswordChanged"));
        }
        finally { barrier.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Password_winning_before_delete_remains_in_history_without_advancing_snapshot_version()
    {
        var barrier = new SelfCommitBarrier();
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var player = await ClientAsync(app); using var admin = await ClientAsync(app, true); var id = await IdAsync(app); barrier.Target = id; barrier.OriginalVersion = await VersionAsync(app, id); barrier.Armed = true;
        var changing = ChangeAsync(player, BootstrapTests.PlayerPassword, "WinnerPassword1!"); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            var deleting = admin.DeleteAsync(Admin + id); var deadline = DateTime.UtcNow.AddSeconds(2);
            while ((long)(await app.ScalarAsync("SELECT count(*) FROM pg_stat_activity WHERE datname=current_database() AND wait_event_type='Lock' AND query LIKE '%FOR UPDATE%'"))! == 0)
            { Assert.True(DateTime.UtcNow < deadline, "Delete must actually wait for password commit"); await Task.Delay(10); }
            barrier.Release.TrySetResult(); Assert.Equal(HttpStatusCode.NoContent, (await changing).StatusCode); Assert.Equal(HttpStatusCode.NoContent, (await deleting).StatusCode);
            using var scope = app.Services.CreateScope(); Assert.True(scope.ServiceProvider.GetRequiredService<IPasswordHasher>().Verify(await HashAsync(app, id), "WinnerPassword1!"));
            Assert.Equal(2L, await StateVersionAsync(app, id)); Assert.Equal(true, await app.ScalarAsync("SELECT \"IsDeleted\" FROM \"Users\" WHERE \"Id\"=@id", P(id)));
        }
        finally { barrier.Release.TrySetResult(); }
    }

    [Fact]
    public async Task Two_password_changes_from_same_snapshot_have_one_winner_and_no_lost_update()
    {
        var barrier = new SelfLoadBarrier(2);
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var player = await ClientAsync(app); var id = await IdAsync(app); barrier.Armed = true;
        var tasks = new[] { ChangeAsync(player, BootstrapTests.PlayerPassword, "FirstFixture1!"), ChangeAsync(player, BootstrapTests.PlayerPassword, "SecondFixture1!") };
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); barrier.Release.TrySetResult(); var responses = await Task.WhenAll(tasks);
        Assert.Single(responses, x => x.StatusCode == HttpStatusCode.NoContent); var loser = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        Assert.Equal("concurrency_conflict", (await JsonAsync(loser)).GetProperty("code").GetString());
        using var scope = app.Services.CreateScope(); var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>(); var hash = await HashAsync(app, id);
        Assert.True(hasher.Verify(hash, responses[0].StatusCode == HttpStatusCode.NoContent ? "FirstFixture1!" : "SecondFixture1!"));
        Assert.False(hasher.Verify(hash, responses[0].StatusCode == HttpStatusCode.NoContent ? "SecondFixture1!" : "FirstFixture1!"));
        Assert.Equal(1L, await StateVersionAsync(app, id)); foreach (var response in responses) response.Dispose();
    }

    [Fact]
    public async Task Profile_and_password_competing_from_same_snapshot_do_not_overwrite_each_other()
    {
        var barrier = new SelfLoadBarrier(2);
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var player = await ClientAsync(app); var id = await IdAsync(app); barrier.Armed = true;
        var password = ChangeAsync(player, BootstrapTests.PlayerPassword, "CompetingFixture1!"); var profile = player.PatchAsJsonAsync(Me, new { name = "CompetingProfile", email = "competing@example.com" });
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); barrier.Release.TrySetResult(); using var passwordResult = await password; using var profileResult = await profile;
        Assert.Equal(1, new[] { passwordResult, profileResult }.Count(x => x.StatusCode == HttpStatusCode.Conflict));
        using var scope = app.Services.CreateScope(); var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
        if (passwordResult.StatusCode == HttpStatusCode.NoContent)
        { Assert.Equal(HttpStatusCode.Conflict, profileResult.StatusCode); Assert.True(hasher.Verify(await HashAsync(app, id), "CompetingFixture1!")); Assert.Equal(1L, await StateVersionAsync(app, id)); }
        else
        { Assert.Equal(HttpStatusCode.OK, profileResult.StatusCode); Assert.True(hasher.Verify(await HashAsync(app, id), BootstrapTests.PlayerPassword)); Assert.Equal(2L, await StateVersionAsync(app, id)); }
    }

    [Fact]
    public async Task Failed_password_commit_rolls_back_hash_and_logs_only_safe_error()
    {
        var failure = new SelfCommitBarrier(fail: true);
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(failure)));
        using var player = await ClientAsync(app); var id = await IdAsync(app); var before = await SnapshotAsync(app, id); failure.Target = id; failure.OriginalVersion = await VersionAsync(app, id); failure.Armed = true;
        await ErrorAsync(await ChangeAsync(player, BootstrapTests.PlayerPassword, "RollbackFixture1!"), HttpStatusCode.InternalServerError, "internal_error");
        Assert.True(failure.SawWrite); Assert.Equal(before, await SnapshotAsync(app, id)); Assert.DoesNotContain(app.Logs.Entries, x => x.Contains("UserPasswordChanged"));
        Assert.DoesNotContain("fixture-password-private-secret", string.Join('\n', app.Logs.Entries));
        Assert.Equal(HttpStatusCode.NoContent, (await ChangeAsync(player, BootstrapTests.PlayerPassword, "RetryFixture1!")).StatusCode);
    }

    [Fact]
    public async Task Authentication_and_state_outage_precede_password_action()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); using var anonymous = app.CreateClient(); var id = await IdAsync(app); var before = await SnapshotAsync(app, id);
        await ErrorAsync(await ChangeAsync(anonymous, BootstrapTests.PlayerPassword, "ChangedFixture1!"), HttpStatusCode.Unauthorized, "unauthenticated");
        await fixture.PauseAsync();
        try
        {
            using var response = await ChangeAsync(player, BootstrapTests.PlayerPassword, "ChangedFixture1!"); Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
            Assert.Equal("1", response.Headers.RetryAfter?.ToString()); Assert.Equal("auth_state_unavailable", (await JsonAsync(response)).GetProperty("code").GetString());
            using var invalid = app.CreateClient(); invalid.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "invalid-token");
            Assert.Equal(HttpStatusCode.Unauthorized, (await ChangeAsync(invalid, BootstrapTests.PlayerPassword, "ChangedFixture1!")).StatusCode);
        }
        finally { await fixture.UnpauseAsync(); }
        Assert.Equal(before, await SnapshotAsync(app, id));
    }

    private const string Me = "/api/v1/me", Password = Me + "/password", Login = "/api/v1/auth/login", Admin = "/api/v1/admin/users/";
    private async Task<AuthFactory> StartAsync(Action<IServiceCollection>? customize = null)
    { var app = await fixture.CreateAsync(customize: customize); BootstrapTests.Configure(app); return app; }
    private static async Task<HttpClient> ClientAsync(AuthFactory app, bool admin = false)
    {
        var client = app.CreateClient(); using var login = await client.PostAsJsonAsync(Login, new { email = admin ? BootstrapTests.AdminEmail : BootstrapTests.PlayerEmail, password = admin ? BootstrapTests.AdminPassword : BootstrapTests.PlayerPassword });
        login.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await JsonAsync(login)).GetProperty("accessToken").GetString()); return client;
    }
    private static Task<HttpResponseMessage> ChangeAsync(HttpClient client, string? currentPassword, string? newPassword) => client.PatchAsJsonAsync(Password, new { currentPassword, newPassword });
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();
    private static async Task ErrorAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    { using (response) { Assert.Equal(status, response.StatusCode); Assert.Equal(code, (await JsonAsync(response)).GetProperty("code").GetString()); } }
    private static NpgsqlParameter P(Guid id) => new("id", id);
    private static async Task<Guid> IdAsync(AuthFactory app, string email = BootstrapTests.PlayerEmail) => (Guid)(await app.ScalarAsync("SELECT \"Id\" FROM \"Users\" WHERE \"Email\"=@email", new NpgsqlParameter("email", email)))!;
    private static async Task<string> HashAsync(AuthFactory app, Guid id) => (string)(await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\" WHERE \"Id\"=@id", P(id)))!;
    private static async Task<string> VersionAsync(AuthFactory app, Guid id) => (string)(await app.ScalarAsync("SELECT xmin::text FROM \"Users\" WHERE \"Id\"=@id", P(id)))!;
    private static async Task<long> StateVersionAsync(AuthFactory app, Guid id) => (long)(await app.ScalarAsync("SELECT \"StateVersion\" FROM \"Users\" WHERE \"Id\"=@id", P(id)))!;
    private static async Task<string> SnapshotAsync(AuthFactory app, Guid id) => (string)(await app.ScalarAsync("SELECT row_to_json(t)::text FROM (SELECT *,xmin::text token FROM \"Users\" WHERE \"Id\"=@id)t", P(id)))!;

    private sealed class SelfLoadBarrier(int readers) : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        private int count;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("self-edit-load"))
            { if (Interlocked.Increment(ref count) == readers) Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }
    private sealed class SelfCommitBarrier(bool fail = false) : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public Guid Target { get; set; }
        public string OriginalVersion { get; set; } = ""; public bool SawWrite { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result; await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT count(*) FROM \"Users\" WHERE \"Id\"=@id AND xmin::text<>@version";
            command.Parameters.Add(P(Target)); command.Parameters.Add(new NpgsqlParameter("version", OriginalVersion));
            if ((long)(await command.ExecuteScalarAsync(cancellationToken))! == 0) return result;
            Armed = false; SawWrite = true; if (fail) throw new InvalidOperationException("fixture-password-private-secret");
            Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); return result;
        }
    }
}
