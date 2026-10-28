using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json;
using Xunit;
using static FCG.Users.Api.Tests.Authentication.RegistrationTests;

namespace FCG.Users.Api.Tests.Authentication;

[Collection("auth-postgres")]
public sealed class JwtConfigurationTests(AuthFixture fixture)
{
    [Theory]
    [InlineData("Jwt:Issuer", "")]
    [InlineData("Jwt:Audience", " ")]
    [InlineData("Jwt:Algorithm", "HS256")]
    [InlineData("Jwt:ExpirationMinutes", "60")]
    [InlineData("Jwt:ClockSkewSeconds", "1")]
    [InlineData("Jwt:PrivateKeyPath", "")]
    [InlineData("Jwt:PublicKeyPath", "")]
    public async Task Invalid_identity_configuration_rejects_startup_instead_of_silently_changing_contract(string key, string value)
    {
        await using var app = await fixture.CreateAsync();
        app.Overrides[key] = value;
        var error = Record.Exception(() => { using var client = app.CreateClient(); });
        Assert.NotNull(error);
        Assert.Contains("JWT", error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("BEGIN PRIVATE KEY", error.ToString());
    }

    [Theory]
    [InlineData("missing-private")]
    [InlineData("missing-public")]
    [InlineData("private-pkcs1")]
    [InlineData("public-holds-private")]
    [InlineData("private-holds-public")]
    [InlineData("mismatched-pair")]
    [InlineData("weak-1024")]
    [InlineData("malformed-private")]
    [InlineData("duplicate-private")]
    [InlineData("same-file")]
    public async Task Invalid_key_material_rejects_startup_without_generating_replacement_or_leaking_private_material(string scenario)
    {
        var directory = Path.Combine(Path.GetTempPath(), "fcg-users-invalid-keys-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var rsa = RSA.Create(scenario == "weak-1024" ? 1024 : 2048);
            var privatePem = rsa.ExportPkcs8PrivateKeyPem();
            var publicPem = rsa.ExportSubjectPublicKeyInfoPem();
            var privatePath = Path.Combine(directory, "private.pem");
            var publicPath = Path.Combine(directory, "public.pem");
            await File.WriteAllTextAsync(privatePath, privatePem);
            await File.WriteAllTextAsync(publicPath, publicPem);
            switch (scenario)
            {
                case "missing-private": File.Delete(privatePath); break;
                case "missing-public": File.Delete(publicPath); break;
                case "private-pkcs1": await File.WriteAllTextAsync(privatePath, rsa.ExportRSAPrivateKeyPem()); break;
                case "public-holds-private": await File.WriteAllTextAsync(publicPath, privatePem); break;
                case "private-holds-public": await File.WriteAllTextAsync(privatePath, publicPem); break;
                case "mismatched-pair":
                    using (var other = RSA.Create(2048)) await File.WriteAllTextAsync(publicPath, other.ExportSubjectPublicKeyInfoPem());
                    break;
                case "malformed-private": await File.WriteAllTextAsync(privatePath, "invalid-sensitive-private-content"); break;
                case "duplicate-private": await File.WriteAllTextAsync(privatePath, privatePem + "\n" + privatePem); break;
                case "same-file": publicPath = privatePath; break;
            }
            var originals = Directory.GetFiles(directory).ToDictionary(path => path, File.ReadAllBytes);
            await using var app = await fixture.CreateAsync();
            app.Overrides["Jwt:PrivateKeyPath"] = privatePath;
            app.Overrides["Jwt:PublicKeyPath"] = publicPath;
            var error = Record.Exception(() => { using var client = app.CreateClient(); });
            Assert.NotNull(error);
            Assert.Contains("JWT", error.ToString(), StringComparison.OrdinalIgnoreCase);
            var diagnostics = error + "\n" + string.Join('\n', app.Logs.Entries);
            Assert.DoesNotContain(privatePem, diagnostics);
            Assert.DoesNotContain("BEGIN PRIVATE KEY", diagnostics);
            Assert.DoesNotContain("invalid-sensitive-private-content", diagnostics);
            Assert.Equal(originals.Keys.Order(StringComparer.Ordinal), Directory.GetFiles(directory).Order(StringComparer.Ordinal));
            foreach (var file in originals) Assert.Equal(file.Value, await File.ReadAllBytesAsync(file.Key));
        }
        finally { Directory.Delete(directory, recursive: true); }
    }

    [Fact]
    public async Task Restart_keeps_persisted_keys_and_a_previously_issued_token_valid()
    {
        var privatePath = Path.Combine(fixture.KeyDirectory, "private.pem");
        var publicPath = Path.Combine(fixture.KeyDirectory, "public.pem");
        var privateBefore = await File.ReadAllBytesAsync(privatePath);
        var publicBefore = await File.ReadAllBytesAsync(publicPath);
        string connectionString;
        string token;
        await using (var first = await fixture.CreateAsync())
        {
            connectionString = first.ConnectionString;
            using var client = first.CreateClient();
            using var registered = await RegisterAsync(client);
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            using var login = await client.PostAsJsonAsync(LoginTests.Endpoint, new { email = "person@example.com", password = Password });
            Assert.Equal(HttpStatusCode.OK, login.StatusCode);
            token = (await login.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
        }
        await using (var second = new AuthFactory(connectionString, fixture.KeyDirectory))
        {
            using var client = second.CreateClient();
            client.DefaultRequestHeaders.Authorization = new("Bearer", token);
            using var protectedResponse = await client.GetAsync("/test-auth/user");
            Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
            Assert.Equal(1L, await second.ScalarAsync("SELECT count(*) FROM \"Users\""));
        }
        Assert.Equal(privateBefore, await File.ReadAllBytesAsync(privatePath));
        Assert.Equal(publicBefore, await File.ReadAllBytesAsync(publicPath));
    }

    [Fact]
    public async Task Disposing_one_host_does_not_break_another_using_the_same_persisted_keys()
    {
        var first = await fixture.CreateAsync();
        try
        {
            using var firstClient = first.CreateClient();
            using var registered = await RegisterAsync(firstClient);
            Assert.Equal(HttpStatusCode.Created, registered.StatusCode);
            using var firstLogin = await firstClient.PostAsJsonAsync(LoginTests.Endpoint,
                new { email = "person@example.com", password = Password });
            Assert.Equal(HttpStatusCode.OK, firstLogin.StatusCode);
            var originalToken = (await firstLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
            firstClient.DefaultRequestHeaders.Authorization = new("Bearer", originalToken);
            using var firstAccess = await firstClient.GetAsync("/test-auth/user");
            Assert.Equal(HttpStatusCode.OK, firstAccess.StatusCode);

            await using var second = new AuthFactory(first.ConnectionString, fixture.KeyDirectory);
            using var secondClient = second.CreateClient();
            secondClient.DefaultRequestHeaders.Authorization = new("Bearer", originalToken);
            using var warmed = await secondClient.GetAsync("/test-auth/user");
            Assert.Equal(HttpStatusCode.OK, warmed.StatusCode);
            using var warmLogin = await secondClient.PostAsJsonAsync(LoginTests.Endpoint,
                new { email = "person@example.com", password = Password });
            Assert.Equal(HttpStatusCode.OK, warmLogin.StatusCode);

            await first.DisposeAsync();
            using var preserved = await secondClient.GetAsync("/test-auth/user");
            Assert.Equal(HttpStatusCode.OK, preserved.StatusCode);
            using var nextLogin = await secondClient.PostAsJsonAsync(LoginTests.Endpoint,
                new { email = "person@example.com", password = Password });
            Assert.Equal(HttpStatusCode.OK, nextLogin.StatusCode);
            var nextToken = (await nextLogin.Content.ReadFromJsonAsync<JsonElement>()).GetProperty("accessToken").GetString()!;
            secondClient.DefaultRequestHeaders.Authorization = new("Bearer", nextToken);
            using var nextAccess = await secondClient.GetAsync("/test-auth/user");
            Assert.Equal(HttpStatusCode.OK, nextAccess.StatusCode);
        }
        finally { await first.DisposeAsync(); }
    }

    [Fact]
    public async Task Enabled_Swagger_documents_authentication_in_Production_without_refresh_or_admin_product_routes()
    {
        await using var app = await fixture.CreateAsync();
        using var client = app.CreateClient();
        using var response = await client.GetAsync("/swagger/v1/swagger.json");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var document = await response.Content.ReadFromJsonAsync<JsonElement>();
        var paths = document.GetProperty("paths");
        var registerResponses = paths.GetProperty("/api/v1/auth/register").GetProperty("post").GetProperty("responses");
        foreach (var code in new[] { "201", "400", "409", "429" }) Assert.True(registerResponses.TryGetProperty(code, out _), code);
        var loginResponses = paths.GetProperty("/api/v1/auth/login").GetProperty("post").GetProperty("responses");
        foreach (var code in new[] { "200", "400", "401", "429" }) Assert.True(loginResponses.TryGetProperty(code, out _), code);
        Assert.DoesNotContain(paths.EnumerateObject(), path => path.Name.Contains("refresh", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(paths.EnumerateObject(), path => path.Name.StartsWith("/api/v1/admin/", StringComparison.Ordinal));
        Assert.False(paths.TryGetProperty("/api/v1/me", out _));
        using var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.OK, ui.StatusCode);
    }

    [Fact]
    public async Task Disabled_Swagger_is_not_exposed_in_Production()
    {
        await using var app = await fixture.CreateAsync();
        app.Overrides["Swagger:Enabled"] = "false";
        using var client = app.CreateClient();
        using var document = await client.GetAsync("/swagger/v1/swagger.json");
        using var ui = await client.GetAsync("/swagger/index.html");
        Assert.Equal(HttpStatusCode.NotFound, document.StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, ui.StatusCode);
        using var registration = await RegisterAsync(client);
        Assert.Equal(HttpStatusCode.Created, registration.StatusCode);
    }
}
