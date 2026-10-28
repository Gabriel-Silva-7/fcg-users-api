using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Npgsql;
using Xunit;
using static FCG.Users.Api.Tests.Authentication.RegistrationTests;
namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class LoginTests(AuthFixture fixture)
{
    internal const string Endpoint = "/api/v1/auth/login";
    [Fact]
    public async Task Login_emits_RS256_without_contact_and_expires_at_exact_boundary()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var registered = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var response = await client.PostAsJsonAsync(Endpoint, new { email = "PERSON@EXAMPLE.COM", password = Password });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(900, body.GetProperty("expiresIn").GetInt32());
        Assert.Equal("Bearer", body.GetProperty("tokenType").GetString());
        Assert.Equal(3, body.EnumerateObject().Count());
        var token = body.GetProperty("accessToken").GetString()!;
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("RS256", jwt.Header.Alg);
        Assert.Equal("fcg-users-api", jwt.Issuer);
        Assert.Equal("fcg-clients", Assert.Single(jwt.Audiences));
        Assert.Equal(TimeSpan.FromMinutes(15), jwt.ValidTo - jwt.ValidFrom);
        Assert.Equal("User", jwt.Claims.Single(x => x.Type == "role").Value);
        Assert.True(Guid.TryParse(jwt.Id, out _));
        Assert.DoesNotContain(jwt.Claims, x => x.Type.Contains("email", StringComparison.OrdinalIgnoreCase));
        client.DefaultRequestHeaders.Authorization = new("Bearer", token);
        using var active = await client.GetAsync("/test-auth/user");
        Assert.Equal(HttpStatusCode.OK, active.StatusCode);
        app.Clock.Now = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero).AddSeconds(-1);
        using var before = await client.GetAsync("/test-auth/user");
        Assert.Equal(HttpStatusCode.OK, before.StatusCode);
        app.Clock.Now = new DateTimeOffset(jwt.ValidTo, TimeSpan.Zero);
        using var expired = await client.GetAsync("/test-auth/user");
        await AssertProblemAsync(expired, HttpStatusCode.Unauthorized, "unauthenticated");
        Assert.DoesNotContain(token, string.Join('\n', app.Logs.Entries));
    }
    [Theory]
    [InlineData("absent")]
    [InlineData("blocked")]
    [InlineData("deleted")]
    public async Task Login_and_access_are_denied_after_account_change(string state)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var registered = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var id = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token(fixture.SigningKey, id));
        if (state == "absent") await app.ExecuteAsync("DELETE FROM \"Users\"");
        else await app.ExecuteAsync("UPDATE \"Users\" SET \"IsActive\" = false, \"IsDeleted\" = @deleted", new NpgsqlParameter("deleted", state == "deleted"));
        using var login = await client.PostAsJsonAsync(Endpoint, new { email = "person@example.com", password = Password });
        await AssertProblemAsync(login, HttpStatusCode.Unauthorized, "invalid_credentials");
        using var access = await client.GetAsync("/test-auth/user");
        await AssertProblemAsync(access, HttpStatusCode.Unauthorized, "unauthenticated");
    }
    [Theory]
    [InlineData("User", "User", 403)]
    [InlineData("Administrator", "User", 403)]
    [InlineData("User", "Administrator", 403)]
    [InlineData("Administrator", "Administrator", 200)]
    public async Task Admin_requires_both_token_and_current_role(string tokenRole, string currentRole, int expected)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var registered = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var id = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        await app.ExecuteAsync("UPDATE \"Users\" SET \"Role\" = @role", new NpgsqlParameter("role", currentRole));
        client.DefaultRequestHeaders.Authorization = new("Bearer", app.Token(fixture.SigningKey, id, tokenRole));
        using var response = await client.GetAsync("/test-auth/admin");
        Assert.Equal(expected, (int)response.StatusCode);
    }
    [Theory]
    [InlineData("missing@example.com", "Str0ng!Pass", 401)]
    [InlineData("person@example.com", "Wr0ng!Pass", 401)]
    [InlineData("not-an-email", "Str0ng!Pass", 401)]
    [InlineData("person@example.com", "", 400)]
    [InlineData(null, null, 400)]
    public async Task Credential_failures_preserve_HTTP_contract(string? email, string? password, int expected)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var registered = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        using var response = await client.PostAsJsonAsync(Endpoint, new { email, password });
        await AssertProblemAsync(response, (HttpStatusCode)expected, expected == 401 ? "invalid_credentials" : "validation_error");
    }
    [Fact]
    public async Task Limits_are_separate_per_policy_and_IP_and_health_is_unlimited()
    {
        await using var app = await fixture.CreateAsync(permits: 2);
        using var client = app.CreateClient();
        for (var i = 0; i < 2; i++)
        {
            using var login = await client.PostAsJsonAsync(Endpoint, new { email = "missing@example.com", password = Password });
            Assert.Equal(HttpStatusCode.Unauthorized, login.StatusCode);
        }
        using var limited = await client.PostAsJsonAsync(Endpoint, new { email = "missing@example.com", password = Password });
        await AssertProblemAsync(limited, HttpStatusCode.TooManyRequests, "rate_limit_exceeded");
        using var registration = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
        using var secondRegistration = await RegisterAsync(client, "second@example.com");
        Assert.Equal(HttpStatusCode.Created, secondRegistration.StatusCode);
        using var limitedRegister = await RegisterAsync(client, "third@example.com");
        await AssertProblemAsync(limitedRegister, HttpStatusCode.TooManyRequests, "rate_limit_exceeded");
        client.DefaultRequestHeaders.Add("X-Test-IP", "192.0.2.2");
        using var otherIp = await client.PostAsJsonAsync(Endpoint, new { email = "person@example.com", password = Password });
        Assert.Equal(HttpStatusCode.OK, otherIp.StatusCode);
        for (var i = 0; i < 4; i++)
        {
            using var live = await client.GetAsync("/health/live");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        }
    }
    [Fact]
    public async Task Production_error_has_trace_and_does_not_leak_exception()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var error = await client.GetAsync("/test-auth/error");
        var body = await AssertProblemAsync(error, HttpStatusCode.InternalServerError, "internal_error");
        var logs = string.Join('\n', app.Logs.Entries);
        Assert.Contains(body.GetProperty("traceId").GetString()!, logs);
        Assert.DoesNotContain("DoNotLeakSecretOrStack", logs);
        Assert.DoesNotContain("DoNotLeakSecretOrStack", body.GetRawText());
        Assert.DoesNotContain("at FCG.", logs);
    }
}
