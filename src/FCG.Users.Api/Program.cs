using FCG.Users.Api;
using FCG.Users.Api.Errors;
using FCG.Users.Api.Logging;
using FCG.Users.Api.Security;
using FCG.Users.Application.Common;
using FCG.Users.Application.Identity;
using FCG.Users.Infrastructure.Common;
using FCG.Users.Infrastructure.Identity;
using FCG.Users.Infrastructure.Persistence;
using FCG.Users.Infrastructure.Messaging;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.OpenApi;

if (args is ["--healthcheck"])
    return await ContainerProbe.RunAsync(new Uri("http://127.0.0.1:8080/health/ready"));

var builder = WebApplication.CreateBuilder(args);
var connection = builder.Configuration.GetConnectionString("Default");
if (string.IsNullOrWhiteSpace(connection)) throw new InvalidOperationException("ConnectionStrings:Default is required for Users.");
builder.Services.AddDbContext<UsersDbContext>(options =>
{
    options.UseNpgsql(connection);
    if (!builder.Environment.IsDevelopment())
    {
        // These EF events include raw exceptions/stack traces. The API records safe
        // status, exception type and traceId instead; keep detailed diagnostics local.
        options.ConfigureWarnings(warnings => warnings.Ignore(
            RelationalEventId.CommandError, RelationalEventId.ConnectionError,
            CoreEventId.QueryIterationFailed, CoreEventId.SaveChangesFailed));
    }
});
builder.Services.AddScoped<IUserRepository, UserRepository>();
builder.Services.AddUsersMessaging(builder.Configuration);
if (!builder.Environment.IsDevelopment())
    builder.Logging.AddFilter("MassTransit", LogLevel.None);
builder.Services.AddSingleton<TimeProvider>(TimeProvider.System);
builder.Services.AddSingleton<IClock, SystemClock>();
builder.Services.AddSingleton<IPasswordHasher, AspNetCorePasswordHasher>();
builder.Services.AddSingleton<IJwtTokenGenerator, JwtTokenGenerator>();
builder.Services.AddScoped<RegisterUserHandler>();
builder.Services.AddScoped<LoginUserHandler>();
builder.Services.AddUsersAuthentication(builder.Configuration);
builder.Services.AddControllers();
builder.Services.AddApiProblemDetails();
builder.Services.AddApiLogging();
builder.Services.AddApiRateLimiting(builder.Configuration);
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(options =>
{
    options.SwaggerDoc("v1", new OpenApiInfo { Title = "FCG Users API", Version = "v1", Description = "Public registration and authentication with RS256 access tokens." });
    options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme { Type = SecuritySchemeType.Http, Scheme = "bearer", BearerFormat = "JWT" });
});
builder.Services.AddHealthChecks().AddCheck<UsersDatabaseHealthCheck>("users-database", tags: ["ready"]);
var app = builder.Build();
// Validate persisted key files before opening the listener, even without an initial login.
_ = app.Services.GetRequiredService<JwtKeyMaterial>();
if (builder.Configuration.GetValue<bool>("Database:MigrateOnStartup"))
{
    await using var scope = app.Services.CreateAsyncScope();
    await scope.ServiceProvider.GetRequiredService<UsersDbContext>().Database.MigrateAsync();
}
app.UseApiRequestLogging();
app.UseExceptionHandler();
app.UseStatusCodePages();
app.UseRouting();
app.UseApiRateLimiting();
app.UseAuthentication();
app.UseAuthorization();
if (builder.Configuration.GetValue<bool>("Swagger:Enabled")) { app.UseSwagger(); app.UseSwaggerUI(); }
app.MapControllers();
app.MapHealthChecks("/health/live", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = check => check.Tags.Contains("ready"),
    ResultStatusCodes = { [HealthStatus.Degraded] = 503, [HealthStatus.Unhealthy] = 503 }
});
app.Run();
return 0;
public partial class Program;
