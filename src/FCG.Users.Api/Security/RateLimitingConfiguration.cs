using System.ComponentModel.DataAnnotations;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace FCG.Users.Api.Security;

public sealed class RateLimitingOptions
{
    public const string SectionName = "RateLimiting";

    [Range(1, int.MaxValue)]
    public int PermitLimit { get; set; } = 10;

    [Range(1, int.MaxValue)]
    public int WindowSeconds { get; set; } = 60;
}

public static class RateLimitingConfiguration
{
    public const string LoginPolicy = "auth-login";

    public const string RegisterPolicy = "auth-register";

    public const string UnknownPartitionKey = "unknown";

    public static IServiceCollection AddApiRateLimiting(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        // Sem validação, um PermitLimit inválido derruba login e register em runtime com 500,
        // e o app sobe normalmente — só as rotas de auth ficam mortas.
        services
            .AddOptions<RateLimitingOptions>()
            .Bind(configuration.GetSection(RateLimitingOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddRateLimiter(options =>
        {
            // O padrão do .NET é 503, que anunciaria indisponibilidade do servidor em vez de
            // excesso do cliente — e 503 nem está no catálogo de erros do projeto.
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

            AddFixedWindowPolicy(options, LoginPolicy);
            AddFixedWindowPolicy(options, RegisterPolicy);
        });

        return services;
    }

    public static IApplicationBuilder UseApiRateLimiting(this IApplicationBuilder app) =>
        app.UseRateLimiter();

    private static void AddFixedWindowPolicy(RateLimiterOptions options, string policyName) =>
        options.AddPolicy(policyName, httpContext =>
        {
            var limits = httpContext.RequestServices
                .GetRequiredService<IOptions<RateLimitingOptions>>().Value;

            return RateLimitPartition.GetFixedWindowLimiter(
                ResolvePartitionKey(httpContext),
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = limits.PermitLimit,
                    Window = TimeSpan.FromSeconds(limits.WindowSeconds),
                });
        });

    // Extraído para ser testável: sob TestServer o RemoteIpAddress é sempre nulo, então a
    // partição por IP nunca é exercitada por teste de integração e uma regressão passaria batida.
    public static string ResolvePartitionKey(HttpContext httpContext) =>
        httpContext.Connection.RemoteIpAddress?.ToString() ?? UnknownPartitionKey;
}
