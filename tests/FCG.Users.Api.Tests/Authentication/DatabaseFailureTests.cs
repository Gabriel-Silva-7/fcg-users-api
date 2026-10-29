using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;
using static FCG.Users.Api.Tests.Authentication.RegistrationTests;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class DatabaseFailureTests(AuthFixture fixture)
{
    [Fact]
    public async Task Database_failures_keep_Production_logs_private_and_recover()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var registered = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var login = await client.PostAsJsonAsync(LoginTests.Endpoint,
            new { email = "person@example.com", password = Password });
        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        var token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        var hash = Assert.IsType<string>(await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\""));
        var connection = new NpgsqlConnectionStringBuilder(app.ConnectionString);
        var problems = new List<JsonElement>();
        await fixture.PauseAsync();
        try
        {
            using var unavailableLogin = await client.PostAsJsonAsync(LoginTests.Endpoint,
                new { email = "person@example.com", password = Password });
            problems.Add(await AssertProblemAsync(unavailableLogin, HttpStatusCode.ServiceUnavailable, "auth_state_unavailable"));
            Assert.Equal(TimeSpan.FromSeconds(1), unavailableLogin.Headers.RetryAfter?.Delta);
            using var unavailableRegistration = await RegisterAsync(client, "outage@example.com");
            problems.Add(await AssertProblemAsync(unavailableRegistration, HttpStatusCode.InternalServerError, "internal_error"));
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            using var unavailableState = await client.GetAsync("/test-auth/user");
            problems.Add(await AssertProblemAsync(unavailableState, HttpStatusCode.ServiceUnavailable, "auth_state_unavailable"));
            Assert.Equal(TimeSpan.FromSeconds(1), unavailableState.Headers.RetryAfter?.Delta);
            client.DefaultRequestHeaders.Authorization = null;
            using var ready = await client.GetAsync("/health/ready");
            using var live = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
        finally { await fixture.UnpauseAsync(); }

        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var recovered = await client.GetAsync("/test-auth/user");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var logs = string.Join('\n', app.Logs.Entries);
        foreach (var problem in problems) Assert.Contains(problem.GetProperty("traceId").GetString()!, logs);
        var diagnostics = logs + string.Join('\n', problems.Select(problem => problem.GetRawText()));
        foreach (var secret in new[] { Password, hash, token, "person@example.com", "outage@example.com", connection.Password!, app.ConnectionString })
            Assert.DoesNotContain(secret, diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("   at ", diagnostics);
        Assert.Contains("UnhandledException", logs);
        Assert.Contains("HttpRequest", logs);
    }
}
