using System.Collections.Concurrent;
using System.IdentityModel.Tokens.Jwt;
using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using Testcontainers.PostgreSql;
using Xunit;
using MassTransit;
using Testcontainers.RabbitMq;

namespace FCG.Users.Api.Tests.Authentication;

[CollectionDefinition("auth-postgres", DisableParallelization = true)]
public sealed class AuthCollection : ICollectionFixture<AuthFixture>;

public sealed class AuthFixture : IAsyncLifetime
{
    private readonly string _brokerPassword = Guid.NewGuid().ToString("N");
    public RabbitMqContainer Broker { get; }
    public AuthFixture()
    {
        Broker = new RabbitMqBuilder()
            .WithImage("rabbitmq:4.3.6-management@sha256:54cb013d76cd1fd6c0a0708f34062ad61c452bb6f016b434c6c33f1a765d2b58")
            .WithUsername("fcg_users").WithPassword(_brokerPassword)
            .WithEnvironment("RABBITMQ_DEFAULT_VHOST", "/fcg").Build();
    }
    public IReadOnlyDictionary<string, string?> BrokerSettings => new Dictionary<string, string?>
    {
        ["RabbitMq:Host"] = Broker.Hostname, ["RabbitMq:Port"] = Broker.GetMappedPublicPort(5672).ToString(),
        ["RabbitMq:VirtualHost"] = "/fcg", ["RabbitMq:Username"] = "fcg_users",
        ["RabbitMq:Password"] = _brokerPassword
    };
    private readonly PostgreSqlContainer _database = new PostgreSqlBuilder().WithImage("postgres:16@sha256:f1c3376c26f2609ab9f29f71f824103fe2fcd8ee0346485cb6122a4f93df6f94").WithUsername("postgres").Build();
    public string KeyDirectory { get; } = Path.Combine(Path.GetTempPath(), "fcg-users-auth-" + Guid.NewGuid().ToString("N"));
    public RSA SigningKey { get; } = RSA.Create(2048);
    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(KeyDirectory);
        await File.WriteAllTextAsync(Path.Combine(KeyDirectory, "private.pem"), SigningKey.ExportPkcs8PrivateKeyPem());
        await File.WriteAllTextAsync(Path.Combine(KeyDirectory, "public.pem"), SigningKey.ExportSubjectPublicKeyInfoPem());
        await _database.StartAsync();
        await Broker.StartAsync();
    }
    public async Task<AuthFactory> CreateAsync(int permits = 1000, Action<IServiceCollection>? customize = null, bool deliveryEnabled = false)
    {
        var name = "usr_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(_database.GetConnectionString());
        await connection.OpenAsync();
        var role = "role_" + name;
        var password = Guid.NewGuid().ToString("N");
        foreach (var sql in new[] { $"CREATE ROLE \"{role}\" LOGIN PASSWORD '{password}' NOSUPERUSER NOCREATEDB NOCREATEROLE", $"CREATE DATABASE \"{name}\" OWNER \"{role}\"", $"REVOKE ALL ON DATABASE \"{name}\" FROM PUBLIC" })
        {
            await using var command = new NpgsqlCommand(sql, connection);
            await command.ExecuteNonQueryAsync();
        }
        var builder = new NpgsqlConnectionStringBuilder(_database.GetConnectionString()) { Database = name, Username = role, Password = password, IncludeErrorDetail = false, Timeout = 2, CommandTimeout = 3 };
        return new AuthFactory(builder.ConnectionString, KeyDirectory, permits, customize, BrokerSettings, deliveryEnabled);
    }
    public Task PauseAsync() => _database.PauseAsync();
    public Task UnpauseAsync() => _database.UnpauseAsync();
    public async Task DisposeAsync()
    {
        await _database.DisposeAsync();
        await Broker.DisposeAsync();
        SigningKey.Dispose();
        Directory.Delete(KeyDirectory, recursive: true);
    }
}

public sealed class AuthFactory(string connectionString, string keyDirectory, int permits = 1000,
    Action<IServiceCollection>? customize = null, IReadOnlyDictionary<string, string?>? brokerSettings = null,
    bool deliveryEnabled = false) : WebApplicationFactory<Program>
{
    public string ConnectionString { get; } = connectionString;
    public MutableClock Clock { get; } = new();
    public CaptureLogs Logs { get; } = new();
    public Dictionary<string, string?> Overrides { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Production");
        var settings = new Dictionary<string, string?>
        {
            ["ConnectionStrings:Default"] = ConnectionString,
            ["Database:MigrateOnStartup"] = "true",
            ["Jwt:Issuer"] = "fcg-users-api", ["Jwt:Audience"] = "fcg-clients",
            ["Jwt:PublicKeyPath"] = Path.Combine(keyDirectory, "public.pem"),
            ["Jwt:PrivateKeyPath"] = Path.Combine(keyDirectory, "private.pem"),
            ["Jwt:ExpirationMinutes"] = "15", ["Jwt:Algorithm"] = "RS256", ["Jwt:ClockSkewSeconds"] = "0",
            ["RateLimiting:PermitLimit"] = permits.ToString(), ["Swagger:Enabled"] = "true",
            ["Logging:LogLevel:Default"] = "Information"
        };
        foreach (var pair in brokerSettings ?? new Dictionary<string, string?>()) settings[pair.Key] = pair.Value;
        foreach (var pair in Overrides) settings[pair.Key] = pair.Value;
        foreach (var pair in settings) builder.UseSetting(pair.Key, pair.Value);
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Clock);
            services.AddLogging(logging => logging.AddProvider(Logs));
            services.AddControllers().AddApplicationPart(typeof(AuthProbeController).Assembly);
            services.AddSingleton<IStartupFilter, TestIpFilter>();
            if (!deliveryEnabled)
            {
                RegistrationOutboxTests.DisableDelivery(services);
                services.Configure<MassTransitHostOptions>(options => options.WaitUntilStarted = true);
            }
            customize?.Invoke(services);
        });
    }
    protected override void Dispose(bool disposing)
    {
        try { base.Dispose(disposing); }
        finally { if (disposing) ClearOwnedPool(); }
    }
    public override async ValueTask DisposeAsync()
    {
        try { await base.DisposeAsync(); }
        finally { ClearOwnedPool(); }
    }
    private void ClearOwnedPool()
    {
        // Every fixture uses a distinct database/role/connection string. Do not retain idle connections
        // for hundreds of disposed hosts, and do not clear pools belonging to another live fixture.
        using var connection = new NpgsqlConnection(ConnectionString);
        NpgsqlConnection.ClearPool(connection);
    }
    public async Task<object?> ScalarAsync(string sql, params NpgsqlParameter[] parameters)
    {
        await using var connection = new NpgsqlConnection(ConnectionString);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand(sql, connection);
        command.Parameters.AddRange(parameters);
        return await command.ExecuteScalarAsync();
    }
    public async Task ExecuteAsync(string sql, params NpgsqlParameter[] parameters) => await ScalarAsync(sql, parameters);
    public string Token(RSA key, Guid id, string role = "User", string issuer = "fcg-users-api", string audience = "fcg-clients",
        Action<List<Claim>>? changeClaims = null, DateTime? notBefore = null, DateTime? expires = null, string algorithm = SecurityAlgorithms.RsaSha256)
    {
        var claims = new List<Claim> { new("sub", id.ToString()), new("role", role), new("jti", Guid.NewGuid().ToString()) };
        changeClaims?.Invoke(claims);
        return new JwtSecurityTokenHandler().WriteToken(new JwtSecurityToken(issuer, audience, claims,
            notBefore ?? Clock.GetUtcNow().UtcDateTime, expires ?? Clock.GetUtcNow().AddMinutes(15).UtcDateTime,
            new SigningCredentials(new RsaSecurityKey(key)
            {
                CryptoProviderFactory = new CryptoProviderFactory { CacheSignatureProviders = false }
            }, algorithm)));
    }
}

public sealed class MutableClock : TimeProvider
{
    public DateTimeOffset Now { get; set; } = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
    public override DateTimeOffset GetUtcNow() => Now;
}
internal sealed class TestIpFilter : IStartupFilter
{
    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
    {
        app.Use(async (context, nextMiddleware) =>
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-IP"].FirstOrDefault() ?? "192.0.2.1");
            await nextMiddleware();
        });
        next(app);
    };
}
[ApiController, Route("test-auth")]
public sealed class AuthProbeController : ControllerBase
{
    [HttpGet("user"), Authorize(Policy = "UserOrAdmin")]
    public IActionResult UserRoute() => Ok(new { subject = User.FindFirst("sub")?.Value });
    [HttpGet("admin"), Authorize(Policy = "AdminOnly")]
    public IActionResult AdminRoute() => Ok();
    [HttpGet("error")]
    public IActionResult Error() => throw new InvalidOperationException("DoNotLeakSecretOrStack");
}
public sealed class CaptureLogs : ILoggerProvider
{
    public ConcurrentQueue<string> Entries { get; } = new();
    public ILogger CreateLogger(string categoryName) => new Logger(Entries, categoryName);
    public void Dispose() { }
    private sealed class Logger(ConcurrentQueue<string> entries, string category) : ILogger
    {
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            entries.Enqueue(category + " " + formatter(state, exception) + (exception is null ? "" : " " + exception));
    }
}
