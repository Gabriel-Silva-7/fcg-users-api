using System.IdentityModel.Tokens.Jwt;
using System.Text.Json;
using FCG.Contracts;
using FCG.Users.Api.Errors;
using FCG.Users.Application.Identity;
using FCG.Users.Domain.Identity;
using FCG.Users.Infrastructure.Identity;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

namespace FCG.Users.Api.Security;

public static class UsersAuthentication
{
    private static readonly object CurrentRole = new();
    private static readonly object StateUnavailable = new();

    public static IServiceCollection AddUsersAuthentication(this IServiceCollection services, IConfiguration configuration)
    {
        var options = configuration.GetSection("Jwt").Get<JwtContractOptions>() ?? new();
        options.Validate(isIssuer: true);
        services.AddSingleton(Microsoft.Extensions.Options.Options.Create(options));
        services.AddSingleton<JwtKeyMaterial>();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<JwtKeyMaterial, TimeProvider>((jwt, keys, clock) =>
            {
                jwt.MapInboundClaims = false;
                jwt.IncludeErrorDetails = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuerSigningKey = true, IssuerSigningKey = keys.ValidationKey,
                    RequireSignedTokens = true, ValidAlgorithms = [SecurityAlgorithms.RsaSha256],
                    ValidateIssuer = true, ValidIssuer = options.Issuer,
                    ValidateAudience = true, ValidAudience = options.Audience,
                    ValidateLifetime = true, RequireExpirationTime = true, ClockSkew = TimeSpan.Zero,
                    NameClaimType = "sub", RoleClaimType = "role",
                    LifetimeValidator = (notBefore, expires, _, _) => notBefore.HasValue && expires.HasValue
                        && notBefore.Value < expires.Value && notBefore.Value <= clock.GetUtcNow().UtcDateTime
                        && clock.GetUtcNow().UtcDateTime < expires.Value
                };
                jwt.Events = new JwtBearerEvents
                {
                    OnTokenValidated = async context =>
                    {
                        // Validate original signed JSON: readers may collapse duplicate properties.
                        var (header, payload) = context.SecurityToken switch
                        {
                            JsonWebToken token => (token.EncodedHeader, token.EncodedPayload),
                            JwtSecurityToken token => (token.RawHeader, token.RawPayload),
                            _ => ("", "")
                        };
                        var principal = context.Principal!;
                        var subjects = principal.FindAll("sub").ToArray();
                        var roles = principal.FindAll("role").ToArray();
                        var ids = principal.FindAll("jti").ToArray();
                        if (!HasUniqueMembers(header) || !HasUniqueMembers(payload)
                            || subjects.Length != 1 || !Guid.TryParse(subjects[0].Value, out var id) || id == Guid.Empty
                            || roles.Length != 1 || roles[0].Value is not ("User" or "Administrator")
                            || ids.Length != 1 || !Guid.TryParse(ids[0].Value, out var tokenId) || tokenId == Guid.Empty)
                        {
                            context.Fail("Invalid identity claims.");
                            return;
                        }
                        try
                        {
                            var user = await context.HttpContext.RequestServices.GetRequiredService<IUserRepository>()
                                .FindByIdAsync(id, context.HttpContext.RequestAborted);
                            if (user is null || !user.IsActive || user.IsDeleted)
                            {
                                context.Fail("Account is unavailable.");
                                return;
                            }
                            context.HttpContext.Items[CurrentRole] = user.Role;
                        }
                        // EF's non-retrying Npgsql strategy wraps transient failures as
                        // InvalidOperationException; preserve the same unavailable-state contract.
                        catch (Exception error) when (error is NpgsqlException or TimeoutException
                            or InvalidOperationException { InnerException: NpgsqlException or TimeoutException })
                        {
                            context.HttpContext.Items[StateUnavailable] = true;
                            context.Fail("Account state is unavailable.");
                        }
                    },
                    OnChallenge = async context =>
                    {
                        if (!context.HttpContext.Items.ContainsKey(StateUnavailable)) return;
                        context.HandleResponse();
                        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
                        context.Response.Headers.RetryAfter = "1";
                        await context.HttpContext.RequestServices.GetRequiredService<IProblemDetailsService>().WriteAsync(new()
                        {
                            HttpContext = context.HttpContext,
                            ProblemDetails = ApiErrors.AuthStateUnavailable.ToProblemDetails(context.Request.Path)
                        });
                    }
                };
            });
        services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(IdentityAuthorizationPolicy.UserOrAdmin, policy => policy.RequireAuthenticatedUser().RequireRole("User", "Administrator"));
            authorization.AddPolicy(IdentityAuthorizationPolicy.AdminOnly, policy => policy.RequireAuthenticatedUser()
                .RequireRole("Administrator").RequireAssertion(context => context.Resource is HttpContext http
                    && http.Items.TryGetValue(CurrentRole, out var role) && role is UserRole.Administrator));
        });
        return services;
    }

    private static bool HasUniqueMembers(string encoded)
    {
        try
        {
            using var json = JsonDocument.Parse(Base64UrlEncoder.DecodeBytes(encoded));
            if (json.RootElement.ValueKind != JsonValueKind.Object) return false;
            var names = new HashSet<string>(StringComparer.Ordinal);
            return json.RootElement.EnumerateObject().All(property => names.Add(property.Name));
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException) { return false; }
    }
}
