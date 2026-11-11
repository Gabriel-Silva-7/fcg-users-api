using System.Net.Sockets;
using Npgsql;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class IdentityReconciliationCliTests(AuthFixture fixture)
{
    [Fact]
    public async Task Dry_run_is_a_secondary_process_without_listener_seed_migration_or_write()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        using var registration = await client.PostAsJsonAsync("/api/v1/auth/register", new { name = "CLI Person", email = "cli-person@example.com", password = "CliFixture1!" });
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
        (int ExitCode, string Output, string Error) result;
        try { result = await RunConfiguredAsync(app, new Dictionary<string, string?> { ["ASPNETCORE_URLS"] = "http://127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port }, "--all", "--dry-run"); }
        finally { listener.Stop(); }
        Assert.Equal(0, result.ExitCode);
        var report = JsonDocument.Parse(result.Output).RootElement;
        Assert.True(report.GetProperty("dryRun").GetBoolean()); Assert.Equal(1, report.GetProperty("selected").GetInt32()); Assert.Equal(0, report.GetProperty("staged").GetInt32());
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\"")); Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Apply_stages_without_running_bus_even_when_broker_address_is_unreachable()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        using var registration = await client.PostAsJsonAsync("/api/v1/auth/register", new { name = "CLI Person", email = "cli-person@example.com", password = "CliFixture1!" }); Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        var before = await app.ScalarAsync("SELECT row_to_json(t)::text FROM (SELECT *,xmin::text token FROM \"Users\")t");
        var settings = new Dictionary<string, string?> { ["RabbitMq__Host"] = "127.0.0.1", ["RabbitMq__Port"] = "1" };
        foreach (var _ in new[] { 1, 2 })
        {
            var result = await RunConfiguredAsync(app, settings, "--all"); Assert.Equal(0, result.ExitCode);
            var report = JsonDocument.Parse(result.Output).RootElement; Assert.False(report.GetProperty("dryRun").GetBoolean()); Assert.Equal(1, report.GetProperty("staged").GetInt32());
            foreach (var secret in new[] { app.ConnectionString, new NpgsqlConnectionStringBuilder(app.ConnectionString).Password!, "cli-person@example.com", "CliFixture1!", "CliUnwantedAdmin1!" }) Assert.DoesNotContain(secret, result.Output + result.Error);
        }
        Assert.Equal(before, await app.ScalarAsync("SELECT row_to_json(t)::text FROM (SELECT *,xmin::text token FROM \"Users\")t"));
        Assert.Equal(3L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
    }

    [Fact]
    public async Task Dry_run_does_not_apply_migrations_or_bootstrap_to_an_empty_database()
    {
        await using var app = await fixture.CreateAsync(); // Database/role exist; no API host or schema has started.
        var result = await RunAsync(app, "--all", "--dry-run"); Assert.Equal(1, result.ExitCode); Assert.Contains("Identity reconciliation failed:", result.Error);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema='public'"));
        Assert.DoesNotContain("   at ", result.Error); Assert.DoesNotContain(app.ConnectionString, result.Error);
    }

    [Fact]
    public async Task Invalid_arguments_are_safe_and_individual_absence_has_a_distinct_exit_code()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient();
        const string secret = "do-not-echo-fixture-secret@example.com";
        var invalid = await RunAsync(app, "--all", "--unsupported=" + secret); Assert.Equal(2, invalid.ExitCode); Assert.DoesNotContain(secret, invalid.Output + invalid.Error); Assert.Contains("Usage:", invalid.Error);
        var missing = await RunAsync(app, "--user-id", Guid.NewGuid().ToString()); Assert.Equal(3, missing.ExitCode);
        Assert.True(JsonDocument.Parse(missing.Output).RootElement.GetProperty("notFound").GetBoolean());
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Users\"")); Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"OutboxMessage\""));
    }

    [Fact]
    public async Task Misspelled_command_cannot_start_the_http_host_or_seed()
    {
        await using var app = await fixture.CreateAsync();
        var result = await RunCommandAsync(app, "reconcile-identity", null, "--all"); Assert.Equal(2, result.ExitCode); Assert.Contains("Usage:", result.Error);
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM information_schema.tables WHERE table_schema='public'"));
    }

    private Task<(int ExitCode, string Output, string Error)> RunAsync(AuthFactory app, params string[] args) => RunConfiguredAsync(app, null, args);

    private Task<(int ExitCode, string Output, string Error)> RunConfiguredAsync(AuthFactory app, IReadOnlyDictionary<string, string?>? extra, params string[] args) => RunCommandAsync(app, "reconcile-identities", extra, args);

    private async Task<(int ExitCode, string Output, string Error)> RunCommandAsync(AuthFactory app, string command, IReadOnlyDictionary<string, string?>? extra, params string[] args)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        start.ArgumentList.Add(typeof(Program).Assembly.Location); start.ArgumentList.Add(command); foreach (var arg in args) start.ArgumentList.Add(arg);
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings__Default"] = app.ConnectionString,
            ["Database__MigrateOnStartup"] = "true",
            ["ASPNETCORE_ENVIRONMENT"] = "Production",
            ["Jwt__Issuer"] = "fcg-users-api",
            ["Jwt__Audience"] = "fcg-clients",
            ["Jwt__PrivateKeyPath"] = Path.Combine(fixture.KeyDirectory, "private.pem"),
            ["Jwt__PublicKeyPath"] = Path.Combine(fixture.KeyDirectory, "public.pem"),
            ["AdminBootstrap__Enabled"] = "true",
            ["AdminBootstrap__Email"] = "cli-unwanted-admin@example.com",
            ["AdminBootstrap__Password"] = "CliUnwantedAdmin1!",
            ["AdminBootstrap__PlayerEmail"] = "cli-unwanted-player@example.com",
            ["AdminBootstrap__PlayerPassword"] = "CliUnwantedPlayer1!"
        };
        foreach (var pair in fixture.BrokerSettings) settings[pair.Key.Replace(":", "__")] = pair.Value;
        foreach (var pair in settings) start.Environment[pair.Key] = pair.Value;
        start.Environment["DOTNET_ENVIRONMENT"] = "Production";
        if (extra is not null) foreach (var pair in extra) start.Environment[pair.Key] = pair.Value;
        using var process = Process.Start(start)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(25)); return (process.ExitCode, await output, await error); }
        finally { if (!process.HasExited) process.Kill(entireProcessTree: true); }
    }
}
