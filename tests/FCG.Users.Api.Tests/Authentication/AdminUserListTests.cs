using System.Data;
using System.Data.Common;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FCG.Users.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class AdminUserListTests(AuthFixture fixture)
{
    [Fact]
    public async Task Administrator_lists_accounts_and_obtains_usable_opaque_status_versions()
    {
        await using var app = await StartAsync(); using var client = await AdminAsync(app);
        var body = await PageAsync(client);
        Assert.Equal(new[] { "items", "page", "pageSize", "totalCount" }, body.EnumerateObject().Select(x => x.Name).Order().ToArray());
        Assert.Equal(1, body.GetProperty("page").GetInt32()); Assert.Equal(20, body.GetProperty("pageSize").GetInt32()); Assert.Equal(2, body.GetProperty("totalCount").GetInt32());
        var user = body.GetProperty("items").EnumerateArray().Single(x => x.GetProperty("email").GetString() == BootstrapTests.PlayerEmail);
        Assert.Equal(new[] { "createdAt", "email", "id", "isActive", "isDeleted", "name", "role", "version" }, user.EnumerateObject().Select(x => x.Name).Order().ToArray());
        var version = user.GetProperty("version").GetString()!; Assert.True(uint.TryParse(version, out _));
        var id = user.GetProperty("id").GetGuid();
        using var patch = await client.PatchAsJsonAsync(Endpoint + "/" + id + "/status", new { isActive = false, version }); Assert.Equal(HttpStatusCode.OK, patch.StatusCode);
        var next = (await PageAsync(client, "?search=bootstrap-player")).GetProperty("items")[0];
        Assert.NotEqual(version, next.GetProperty("version").GetString()); Assert.False(next.GetProperty("isActive").GetBoolean()); Assert.False(next.GetProperty("isDeleted").GetBoolean());
        using var stale = await client.PatchAsJsonAsync(Endpoint + "/" + id + "/status", new { isActive = true, version }); Assert.Equal(HttpStatusCode.Conflict, stale.StatusCode);
        Assert.Equal("concurrency_conflict", (await JsonAsync(stale)).GetProperty("code").GetString());
        Assert.Equal(HttpStatusCode.NoContent, (await client.DeleteAsync(Endpoint + "/" + id)).StatusCode);
        var deleted = (await PageAsync(client, "?search=bootstrap-player")).GetProperty("items")[0];
        Assert.True(deleted.GetProperty("isDeleted").GetBoolean()); Assert.False(deleted.GetProperty("isActive").GetBoolean());
        Assert.Equal(2, (await PageAsync(client)).GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("  gAbRiEl  ", 2)] [InlineData("RELACAO@EXAMPLE", 1)] [InlineData("%", 1)]
    [InlineData("_", 1)] [InlineData("O'Neil", 1)] [InlineData("' OR 1=1 --", 0)] [InlineData("no-match", 0)]
    public async Task Search_is_partial_trimmed_case_insensitive_and_metacharacters_are_literal(string search, int expected)
    {
        await using var app = await StartAsync(); using var client = await AdminAsync(app);
        foreach (var (name, email) in new[] { ("Gabriel Silva", "gabriel@example.com"), ("Gabriela Souza", "gabriela@example.com"),
            ("Sem Relacao", "sem.relacao@example.com"), ("Percent% literal", "percent@example.com"),
            ("Under_score", "under@example.com"), ("O'Neil", "oneil@example.com") })
            Assert.Equal(HttpStatusCode.Created, (await client.PostAsJsonAsync("/api/v1/auth/register", new { name, email, password = "FixtureStrong1!" })).StatusCode);
        var page = await PageAsync(client, "?search=" + Uri.EscapeDataString(search));
        Assert.Equal(expected, page.GetProperty("totalCount").GetInt32()); Assert.Equal(expected, page.GetProperty("items").GetArrayLength());
        Assert.Equal(8, (await PageAsync(client, "?search=%20%20")).GetProperty("totalCount").GetInt32());
        var logs = string.Join('\n', app.Logs.Entries); Assert.DoesNotContain("FixtureStrong1!", logs); Assert.DoesNotContain("sem.relacao@example.com", logs);
    }

    [Fact]
    public async Task Stable_creation_id_order_and_pages_include_deleted_without_credential_fields()
    {
        await using var app = await StartAsync(); using var client = await AdminAsync(app);
        var ids = new[] { Guid.Parse("00000000-0000-0000-0000-000000000001"), Guid.Parse("00000000-0000-0000-0000-000000000002"), Guid.Parse("00000000-0000-0000-0000-000000000003") };
        // Explicit read fixtures: insert reverse order with equal creation time; sort must use Id, not insertion/name.
        foreach (var index in new[] { 2, 1, 0 })
            await app.ExecuteAsync("INSERT INTO \"Users\" (\"Id\",\"Name\",\"Email\",\"PasswordHash\",\"Role\",\"IsActive\",\"IsDeleted\",\"StateVersion\",\"CreatedAtUtc\") VALUES (@id,@name,@email,'fixture-private-hash','User',@active,@deleted,1,@at)",
                new NpgsqlParameter("id", ids[index]), new NpgsqlParameter("name", "ListFixture " + (3-index)), new NpgsqlParameter("email", "list-fixture" + index + "@example.com"),
                new NpgsqlParameter("active", index == 0), new NpgsqlParameter("deleted", index == 2), new NpgsqlParameter("at", new DateTime(2020,1,1,0,0,0,DateTimeKind.Utc)));
        for (var index = 0; index < 3; index++)
        {
            var page = await PageAsync(client, "?search=ListFixture&page=" + (index+1) + "&pageSize=1");
            Assert.Equal(3, page.GetProperty("totalCount").GetInt32()); Assert.Equal(index+1, page.GetProperty("page").GetInt32()); Assert.Equal(1, page.GetProperty("pageSize").GetInt32());
            var item = page.GetProperty("items")[0]; Assert.Equal(ids[index], item.GetProperty("id").GetGuid());
            Assert.Equal(index == 0, item.GetProperty("isActive").GetBoolean()); Assert.Equal(index == 2, item.GetProperty("isDeleted").GetBoolean());
            Assert.False(item.TryGetProperty("passwordHash", out _)); Assert.False(item.TryGetProperty("stateVersion", out _));
        }
        var beyond = await PageAsync(client, "?search=ListFixture&page=1000000&pageSize=100");
        Assert.Equal(0, beyond.GetProperty("items").GetArrayLength()); Assert.Equal(3, beyond.GetProperty("totalCount").GetInt32());
        Assert.Equal(5, (await PageAsync(client, "?userId=" + ids[0] + "&isDeleted=false")).GetProperty("totalCount").GetInt32());
    }

    [Theory]
    [InlineData("?page=0")] [InlineData("?page=-1")] [InlineData("?page=1000001")] [InlineData("?page=word")]
    [InlineData("?pageSize=0")] [InlineData("?pageSize=101")] [InlineData("?pageSize=2147483648")]
    public async Task Invalid_pagination_is_400(string query)
    {
        await using var app = await StartAsync(); using var client = await AdminAsync(app);
        using var response = await client.GetAsync(Endpoint + query); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("validation_error", (await JsonAsync(response)).GetProperty("code").GetString());
    }

    [Fact]
    public async Task Search_length_boundary_is_enforced_before_trim()
    {
        await using var app = await StartAsync(); using var client = await AdminAsync(app);
        Assert.Equal(0, (await PageAsync(client, "?search=" + new string('x',256))).GetProperty("totalCount").GetInt32());
        using var response = await client.GetAsync(Endpoint + "?search=" + new string('x',257)); Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        using var whitespace = await client.GetAsync(Endpoint + "?search=" + Uri.EscapeDataString(new string(' ',257))); Assert.Equal(HttpStatusCode.BadRequest, whitespace.StatusCode);
    }

    [Fact]
    public async Task Authorization_precedes_filter_validation_and_respects_current_role()
    {
        await using var app = await StartAsync(); using var admin = await AdminAsync(app); using var anonymous = app.CreateClient(); using var player = app.CreateClient();
        using var login = await player.PostAsJsonAsync("/api/v1/auth/login", new { email = BootstrapTests.PlayerEmail, password = BootstrapTests.PlayerPassword });
        player.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await JsonAsync(login)).GetProperty("accessToken").GetString());
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonymous.GetAsync(Endpoint + "?page=-1")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await player.GetAsync(Endpoint + "?page=-1")).StatusCode);
        await app.ExecuteAsync("UPDATE \"Users\" SET \"Role\"='User' WHERE \"Email\"='bootstrap-admin@example.com'");
        Assert.Equal(HttpStatusCode.Forbidden, (await admin.GetAsync(Endpoint)).StatusCode);
    }

    [Fact]
    public async Task State_outage_is_503_and_swagger_exposes_only_protected_admin_listing()
    {
        await using var app = await StartAsync(); using var admin = await AdminAsync(app);
        using var swagger = await admin.GetAsync("/swagger/v1/swagger.json");
        var operation = (await JsonAsync(swagger)).GetProperty("paths").GetProperty(Endpoint).GetProperty("get");
        Assert.True(operation.GetProperty("security")[0].TryGetProperty("Bearer", out _));
        foreach (var code in new[] { "200", "400", "401", "403", "503" }) Assert.True(operation.GetProperty("responses").TryGetProperty(code, out _));
        await fixture.PauseAsync();
        try
        {
            using var unavailable = await admin.GetAsync(Endpoint); Assert.Equal(HttpStatusCode.ServiceUnavailable, unavailable.StatusCode);
            Assert.Equal("auth_state_unavailable", (await JsonAsync(unavailable)).GetProperty("code").GetString()); Assert.Equal("1", unavailable.Headers.RetryAfter?.ToString());
        }
        finally { await fixture.UnpauseAsync(); }
    }

    [Fact]
    public async Task Count_and_rows_share_a_readonly_repeatable_snapshot_during_registration()
    {
        var barrier = new CountBarrier();
        await using var app = await StartAsync(services => services.AddDbContext<UsersDbContext>(options => options.AddInterceptors(barrier)));
        using var client = await AdminAsync(app); barrier.Armed = true;
        var first = PageAsync(client); await barrier.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        try
        {
            using var registered = await client.PostAsJsonAsync("/api/v1/auth/register", new { name = "Concurrent", email = "concurrent@example.com", password = "ConcurrentFixture1!" });
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode); barrier.Release.TrySetResult();
            var page = await first; Assert.Equal(2, page.GetProperty("totalCount").GetInt32()); Assert.Equal(2, page.GetProperty("items").GetArrayLength());
            Assert.Equal("on:repeatable read", barrier.Settings);
            var next = await PageAsync(client); Assert.Equal(3, next.GetProperty("totalCount").GetInt32()); Assert.Equal(3, next.GetProperty("items").GetArrayLength());
        }
        finally { barrier.Release.TrySetResult(); }
    }

    private const string Endpoint = "/api/v1/admin/users";
    private async Task<AuthFactory> StartAsync(Action<IServiceCollection>? customize = null)
    { var app = await fixture.CreateAsync(customize: customize); BootstrapTests.Configure(app); return app; }
    private static async Task<HttpClient> AdminAsync(AuthFactory app)
    {
        var client = app.CreateClient(); using var login = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = BootstrapTests.AdminEmail, password = BootstrapTests.AdminPassword });
        login.EnsureSuccessStatusCode(); client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", (await JsonAsync(login)).GetProperty("accessToken").GetString()); return client;
    }
    private static async Task<JsonElement> PageAsync(HttpClient client, string query = "")
    { using var response = await client.GetAsync(Endpoint + query); Assert.Equal(HttpStatusCode.OK, response.StatusCode); return await JsonAsync(response); }
    private static async Task<JsonElement> JsonAsync(HttpResponseMessage response) => await response.Content.ReadFromJsonAsync<JsonElement>();

    private sealed class CountBarrier : DbCommandInterceptor
    {
        public bool Armed { get; set; } private int entered;
        public string? Settings { get; private set; }
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command, CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("Users"))
            {
                await using var settings = command.Connection!.CreateCommand(); settings.Transaction = command.Transaction;
                settings.CommandText = "SELECT current_setting('transaction_read_only') || ':' || current_setting('transaction_isolation')";
                Settings = (string)(await settings.ExecuteScalarAsync(cancellationToken))!;
            }
            return result;
        }
        public override async ValueTask<DbDataReader> ReaderExecutedAsync(DbCommand command, CommandExecutedEventData eventData, DbDataReader result, CancellationToken cancellationToken = default)
        {
            if (Armed && command.CommandText.Contains("count(*)", StringComparison.OrdinalIgnoreCase) && command.CommandText.Contains("Users") && Interlocked.CompareExchange(ref entered, 1, 0) == 0)
            { Entered.TrySetResult(); await Release.Task.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken); }
            return result;
        }
    }
}
