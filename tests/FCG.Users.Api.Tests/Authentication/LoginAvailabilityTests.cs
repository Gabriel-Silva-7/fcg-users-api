using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Xunit;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class LoginAvailabilityTests(AuthFixture fixture)
{
    [Fact]
    public async Task Unavailable_storage_is_not_false_credentials_or_unexpected_error()
    {
        await using var app = await fixture.CreateAsync(); using var client = app.CreateClient(); await fixture.PauseAsync();
        try
        {
            using var response = await client.PostAsJsonAsync("/api/v1/auth/login", new { email = "available-format@example.com", password = "FixturePassword1!" });
            Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal("1", response.Headers.RetryAfter?.ToString());
            Assert.Equal("auth_state_unavailable", (await response.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("code").GetString());
        }
        finally { await fixture.UnpauseAsync(); }
    }
}
