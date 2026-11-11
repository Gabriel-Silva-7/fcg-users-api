using System.Diagnostics;
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FCG.Users.Api.Diagnostics;
using FCG.Users.Application.Common;
using Microsoft.AspNetCore.Routing;

namespace FCG.Users.Api.Logging;

internal sealed class RequestLoggingMiddleware(
    RequestDelegate next,
    ILogger<RequestLoggingMiddleware> logger,
    IClock clock)
{
    private const string UnmatchedRoute = "<unmatched>";

    public async Task InvokeAsync(HttpContext context)
    {
        var timestampUtc = clock.UtcNow;
        var startedAt = Stopwatch.GetTimestamp();

        await next(context);

        logger.LogInformation(
            "HttpRequest {TimestampUtc} {TraceId} {Method} {Route} {StatusCode} {DurationMs} {UserId}",
            timestampUtc,
            TraceIdentity.Resolve(context),
            context.Request.Method,
            ResolveRoute(context),
            context.Response.StatusCode,
            Math.Round(Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds, 2),
            context.User.FindFirstValue(JwtRegisteredClaimNames.Sub));
    }

    private static string ResolveRoute(HttpContext context)
    {
        // O template preserva a rota sem registrar valores enviados nos segmentos da URL.
        var pattern = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText;

        return string.IsNullOrWhiteSpace(pattern)
            ? UnmatchedRoute
            : "/" + pattern.TrimStart('/');
    }
}
