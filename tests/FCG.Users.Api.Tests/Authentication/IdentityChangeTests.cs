using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Contracts;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class IdentityChangeTests(AuthFixture fixture)
{
    [Fact]
    public async Task Effective_profile_change_commits_a_full_state_event_with_the_user()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); var id = await IdAsync(app);
        app.Clock.Now = app.Clock.Now.AddTicks(12347);
        using var updated = await player.PatchAsJsonAsync(Me, new { name = " Updated Contact ", email = " UPDATED-CONTACT@EXAMPLE.COM " });
        Assert.Equal(HttpStatusCode.OK, updated.StatusCode); Assert.Equal(3L, await CountAsync(app));
        var body = (string)(await app.ScalarAsync("SELECT \"Body\" FROM \"OutboxMessage\" WHERE \"Body\"::jsonb->'message'->>'eventVersion'='2'"))!;
        using var envelope = JsonDocument.Parse(body); var root = envelope.RootElement; var payload = root.GetProperty("message");
        var snapshot = IdentityContractJson.Read<UserStateChangedEvent>(payload.GetRawText());
        Assert.Equal(id, snapshot.UserId); Assert.Equal(2, snapshot.EventVersion); Assert.Equal("Updated Contact", snapshot.Name); Assert.Equal("updated-contact@example.com", snapshot.Email);
        Assert.Equal(IdentityRole.User, snapshot.Role); Assert.Equal(IdentityState.Active, snapshot.State); Assert.Equal(0, snapshot.OccurredAtUtc.Ticks % 10);
        Assert.Matches(@"\.\d{6}Z$", payload.GetProperty("occurredAtUtc").GetString()!);
        Assert.Equal(snapshot.EventId, root.GetProperty("messageId").GetGuid()); Assert.Equal(id, root.GetProperty("correlationId").GetGuid());
        Assert.Contains(root.GetProperty("messageType").EnumerateArray(), item => item.GetString() == "urn:message:FCG.Contracts:UserStateChangedEvent");
        Assert.Equal(new[] { "createdAtUtc", "email", "eventId", "eventVersion", "name", "occurredAtUtc", "role", "state", "userId" }, payload.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.DoesNotContain("password", body, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("accessToken", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Status_lifecycle_publishes_only_effective_versions_and_noops_password_or_repeat_delete_do_not()
    {
        await using var app = await StartAsync(); using var player = await ClientAsync(app); using var admin = await ClientAsync(app, true); var id = await IdAsync(app);
        Assert.Equal(HttpStatusCode.OK, (await player.PatchAsJsonAsync(Me, new { name = " Player ", email = "BOOTSTRAP-PLAYER@EXAMPLE.COM" })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await player.PatchAsJsonAsync(Me + "/password", new { currentPassword = BootstrapTests.PlayerPassword, newPassword = "ChangedFixture1!" })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync(Admin + id + "/status", new { isActive = true, version = await VersionAsync(app, id) })).StatusCode);
        Assert.Equal(2L, await CountAsync(app));
        var obsolete = await VersionAsync(app, id);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync(Admin + id + "/status", new { isActive = false, version = obsolete })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await admin.PatchAsJsonAsync(Admin + id + "/status", new { isActive = false, version = obsolete })).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.PatchAsJsonAsync(Admin + id + "/status", new { isActive = true, version = await VersionAsync(app, id) })).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Admin + id)).StatusCode); Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync(Admin + id)).StatusCode);
        Assert.Equal(5L, await CountAsync(app));
        var versions = (string)(await app.ScalarAsync("SELECT string_agg(\"Body\"::jsonb->'message'->>'eventVersion',',' ORDER BY \"SequenceNumber\") FROM \"OutboxMessage\" WHERE (\"Body\"::jsonb->'message'->>'eventVersion')::bigint>1"))!;
        var states = (string)(await app.ScalarAsync("SELECT string_agg(\"Body\"::jsonb->'message'->>'state',',' ORDER BY \"SequenceNumber\") FROM \"OutboxMessage\" WHERE (\"Body\"::jsonb->'message'->>'eventVersion')::bigint>1"))!;
        Assert.Equal("2,3,4", versions); Assert.Equal("Blocked,Active,Deleted", states);
    }

    [Theory]
    [InlineData("Profile")]
    [InlineData("Block")]
    [InlineData("Delete")]
    public async Task Commit_failure_rolls_back_mutation_and_event_together(string operation)
    {
        var fault = new FailStateCommit();
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(fault)));
        using var player = await ClientAsync(app); using var admin = await ClientAsync(app, true); var id = await IdAsync(app); var before = await SnapshotAsync(app, id); var version = await VersionAsync(app, id);
        fault.Target = id; fault.Armed = true;
        Task<HttpResponseMessage> Send() => operation switch
        {
            "Profile" => player.PatchAsJsonAsync(Me, new { name = "Attempted", email = "attempted@example.com" }),
            "Block" => admin.PatchAsJsonAsync(Admin + id + "/status", new { isActive = false, version }),
            _ => admin.DeleteAsync(Admin + id)
        };
        using var failed = await Send(); Assert.Equal(HttpStatusCode.InternalServerError, failed.StatusCode); Assert.True(fault.SawBoth);
        Assert.Equal(before, await SnapshotAsync(app, id)); Assert.Equal(2L, await CountAsync(app));
        Assert.DoesNotContain("fixture-identity-private-secret", string.Join('\n', app.Logs.Entries));
        using var retry = await Send(); Assert.Equal(operation == "Delete" ? HttpStatusCode.NoContent : HttpStatusCode.OK, retry.StatusCode); Assert.Equal(3L, await CountAsync(app));
    }

    [Fact]
    public async Task Concurrent_profile_loser_cannot_leave_a_second_intent_for_the_same_version()
    {
        var barrier = new ProfileBarrier();
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var client = await ClientAsync(app); barrier.Armed = true;
        var changes = new[] { client.PatchAsJsonAsync(Me, new { name = "First", email = "first-event@example.com" }), client.PatchAsJsonAsync(Me, new { name = "Second", email = "second-event@example.com" }) };
        await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); barrier.Release.TrySetResult(); var responses = await Task.WhenAll(changes);
        var winner = Assert.Single(responses, x => x.StatusCode == HttpStatusCode.OK); Assert.Single(responses, x => x.StatusCode == HttpStatusCode.Conflict);
        var email = (await winner.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("email").GetString(); Assert.Equal(3L, await CountAsync(app));
        Assert.Equal(email, await app.ScalarAsync("SELECT \"Body\"::jsonb->'message'->>'email' FROM \"OutboxMessage\" WHERE \"Body\"::jsonb->'message'->>'eventVersion'='2'"));
        foreach (var response in responses) response.Dispose();
    }

    private const string Me = "/api/v1/me", Admin = "/api/v1/admin/users/";
    private async Task<AuthFactory> StartAsync(Action<IServiceCollection>? customize = null)
    { var app = await fixture.CreateAsync(customize: customize); BootstrapTests.Configure(app); return app; }
    private static async Task<HttpClient> ClientAsync(AuthFactory app, bool admin = false)
    {
        var client = app.CreateClient(); using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = admin ? BootstrapTests.AdminEmail : BootstrapTests.PlayerEmail, password = admin ? BootstrapTests.AdminPassword : BootstrapTests.PlayerPassword });
        login.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()); return client;
    }
    private static NpgsqlParameter P(Guid id) => new("id", id);
    private static async Task<Guid> IdAsync(AuthFactory app) => (Guid)(await app.ScalarAsync("SELECT \"Id\" FROM \"Users\" WHERE \"Email\"='bootstrap-player@example.com'"))!;
    private static async Task<long> CountAsync(AuthFactory app) => (long)(await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""))!;
    private static async Task<string> VersionAsync(AuthFactory app, Guid id) => (string)(await app.ScalarAsync("SELECT xmin::text FROM \"Users\" WHERE \"Id\"=@id", P(id)))!;
    private static async Task<string> SnapshotAsync(AuthFactory app, Guid id) => (string)(await app.ScalarAsync("SELECT row_to_json(t)::text FROM (SELECT *,xmin::text token FROM \"Users\" WHERE \"Id\"=@id)t", P(id)))!;
    private sealed class FailStateCommit : DbTransactionInterceptor
    {
        public bool Armed { get; set; }
        public Guid Target { get; set; }
        public bool SawBoth { get; private set; }
        public override async ValueTask<InterceptionResult> TransactionCommittingAsync(DbTransaction transaction, TransactionEventData eventData, InterceptionResult result, CancellationToken cancellationToken = default)
        {
            if (!Armed) return result; await using var command = transaction.Connection!.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "SELECT (SELECT \"StateVersion\" FROM \"Users\" WHERE \"Id\"=@id)=2 AND (SELECT count(*) FROM \"OutboxMessage\" WHERE \"Body\"::jsonb->'message'->>'userId'=@text AND \"Body\"::jsonb->'message'->>'eventVersion'='2')=1";
            command.Parameters.Add(P(Target)); command.Parameters.Add(new NpgsqlParameter("text", Target.ToString()));
            if (!(bool)(await command.ExecuteScalarAsync(cancellationToken))!) return result;
            Armed = false; SawBoth = true; throw new InvalidOperationException("fixture-identity-private-secret");
        }
    }
    private sealed class ProfileBarrier : DbCommandInterceptor
    {
        public bool Armed { get; set; }
        private int reads; public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("self-edit-load")) { if (Interlocked.Increment(ref reads) == 2) Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }
}
