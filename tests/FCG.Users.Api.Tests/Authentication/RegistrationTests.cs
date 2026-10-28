using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;
namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class RegistrationTests(AuthFixture fixture)
{
    internal const string Password = "Str0ng!Pass";
    internal const string Endpoint = "/api/v1/auth/register";
    internal static Task<HttpResponseMessage> RegisterAsync(HttpClient client, string email = "person@example.com") =>
        client.PostAsJsonAsync(Endpoint, new { name = " Person ", email, password = Password, role = "Administrator", isActive = false });
    [Fact]
    public async Task Registration_persists_normalized_regular_user_without_secrets()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var response = await RegisterAsync(client, "  Person@EXAMPLE.com  ");
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        Assert.Equal("/api/v1/me", response.Headers.Location?.OriginalString);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Person", body.GetProperty("name").GetString());
        Assert.Equal("person@example.com", body.GetProperty("email").GetString());
        Assert.Equal("User", body.GetProperty("role").GetString());
        Assert.Equal(4, body.EnumerateObject().Count());
        Assert.NotEqual(Guid.Empty, body.GetProperty("id").GetGuid());
        Assert.Equal(1L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
        var hash = Assert.IsType<string>(await app.ScalarAsync("SELECT \"PasswordHash\" FROM \"Users\""));
        Assert.NotEqual(Password, hash);
        Assert.Equal("User", await app.ScalarAsync("SELECT \"Role\" FROM \"Users\""));
        var logs = string.Join('\n', app.Logs.Entries);
        Assert.DoesNotContain(Password, logs);
        Assert.DoesNotContain(hash, logs);
        Assert.DoesNotContain("person@example.com", logs, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("p***@example.com", logs);
    }
    [Theory]
    [InlineData("", "user@example.com", "Str0ng!Pass")]
    [InlineData("   ", "user@example.com", "Str0ng!Pass")]
    [InlineData("Name", "not-email", "Str0ng!Pass")]
    [InlineData("Name", "user@example.com", "a1!")]
    [InlineData("Name", "user@example.com", "letters!")]
    [InlineData("Name", "user@example.com", "12345678!")]
    [InlineData("Name", "user@example.com", "Abc12345 ")]
    [InlineData(null, null, null)]
    public async Task Invalid_registration_is_400_and_does_not_write(string? name, string? email, string? password)
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var response = await client.PostAsJsonAsync(Endpoint, new { name, email, password });
        await AssertProblemAsync(response, HttpStatusCode.BadRequest, "validation_error");
        Assert.Equal(0L, await app.ScalarAsync("SELECT count(*) FROM \"Users\""));
    }
    internal static async Task<JsonElement> AssertProblemAsync(HttpResponseMessage response, HttpStatusCode status, string code)
    {
        Assert.Equal(status, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(code, problem.GetProperty("code").GetString());
        Assert.Equal((int)status, problem.GetProperty("status").GetInt32());
        Assert.False(string.IsNullOrWhiteSpace(problem.GetProperty("traceId").GetString()));
        return problem;
    }
}
