namespace FCG.Users.Api.Logging;

public static class LoggingConfiguration
{
    private const string ExceptionHandlerCategory =
        "Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware";

    public static IServiceCollection AddApiLogging(this IServiceCollection services)
    {
        // O handler do projeto registra somente tipo e traceId; o padrão imprimiria a exceção inteira.
        services.AddLogging(
            logging => logging.AddFilter(ExceptionHandlerCategory, LogLevel.None));

        return services;
    }

    public static IApplicationBuilder UseApiRequestLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestLoggingMiddleware>();
}
