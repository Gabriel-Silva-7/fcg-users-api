using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using Xunit;
using static FCG.Users.Api.Tests.Authentication.RegistrationTests;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class TokenBoundaryTests(AuthFixture fixture)
{
    [Fact]
    public async Task Invalid_tokens_are_rejected_before_state_lookup_and_valid_token_reports_database_outage()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var registered = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
        var id = (await registered.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("id").GetGuid();
        using var otherKey = RSA.Create(2048);
        var valid = app.Token(fixture.SigningKey, id);
        var invalid = new List<string>
        {
            "bad.token.value", app.Token(otherKey, id),
            app.Token(fixture.SigningKey, id, issuer: "other"), app.Token(fixture.SigningKey, id, audience: "other"),
            app.Token(fixture.SigningKey, id, algorithm: SecurityAlgorithms.RsaSha512),
            app.Token(fixture.SigningKey, id, role: "Root"), app.Token(fixture.SigningKey, Guid.Empty),
            app.Token(fixture.SigningKey, id, notBefore: app.Clock.Now.AddSeconds(1).UtcDateTime),
            app.Token(fixture.SigningKey, id, notBefore: app.Clock.Now.AddMinutes(-16).UtcDateTime, expires: app.Clock.Now.UtcDateTime),
            Rewrite(valid, fixture.SigningKey, json => json[..^1] + ",\"sub\":\"" + id + "\"}"),
            Rewrite(valid, fixture.SigningKey, json => Remove(json, "nbf")),
            Rewrite(valid, fixture.SigningKey, json => Remove(json, "exp"))
        };
        foreach (var claim in new[] { "sub", "role", "jti" })
        {
            invalid.Add(app.Token(fixture.SigningKey, id, changeClaims: claims => claims.RemoveAll(c => c.Type == claim)));
            invalid.Add(app.Token(fixture.SigningKey, id, changeClaims: claims => claims.Add(new Claim(claim, "duplicate"))));
        }
        invalid.Add(app.Token(fixture.SigningKey, id, changeClaims: claims => { claims.RemoveAll(c => c.Type == "jti"); claims.Add(new("jti", Guid.Empty.ToString())); }));
        await fixture.PauseAsync();
        try
        {
            foreach (var token in invalid)
            {
                client.DefaultRequestHeaders.Authorization = new("Bearer", token);
                using var denied = await client.GetAsync("/test-auth/user");
                await AssertProblemAsync(denied, HttpStatusCode.Unauthorized, "unauthenticated");
            }
            client.DefaultRequestHeaders.Authorization = new("Bearer", valid);
            using var unavailable = await client.GetAsync("/test-auth/user");
            await AssertProblemAsync(unavailable, HttpStatusCode.ServiceUnavailable, "auth_state_unavailable");
            Assert.Equal(TimeSpan.FromSeconds(1), unavailable.Headers.RetryAfter?.Delta);
            client.DefaultRequestHeaders.Authorization = null;
            using var live = await client.GetAsync("/health/live");
            using var ready = await client.GetAsync("/health/ready");
            Assert.Equal(HttpStatusCode.OK, live.StatusCode);
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        }
        finally { await fixture.UnpauseAsync(); }
        client.DefaultRequestHeaders.Authorization = new("Bearer", valid);
        using var recovered = await client.GetAsync("/test-auth/user");
        Assert.Equal(HttpStatusCode.OK, recovered.StatusCode);
        var logs = string.Join('\n', app.Logs.Entries);
        Assert.DoesNotContain(valid, logs);
        Assert.DoesNotContain(Password, logs);
    }

    private static string Remove(string json, string field)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;
        values.Remove(field);
        return JsonSerializer.Serialize(values);
    }
    private static string Rewrite(string token, RSA key, Func<string, string> change)
    {
        var parts = token.Split('.');
        var input = parts[0] + "." + Base64UrlEncoder.Encode(change(Base64UrlEncoder.Decode(parts[1])));
        return input + "." + Base64UrlEncoder.Encode(key.SignData(Encoding.UTF8.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
}
