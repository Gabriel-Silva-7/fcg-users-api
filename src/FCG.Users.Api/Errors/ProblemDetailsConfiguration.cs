using FCG.Users.Api.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Users.Api.Errors;

public static class ProblemDetailsConfiguration
{
    public static IServiceCollection AddApiProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(
            options => options.CustomizeProblemDetails = context =>
                ApplyContract(context.HttpContext, context.ProblemDetails));
        // Precisa vir depois de AddControllers/AddProblemDetails: a ordem de registro é a
        // ordem em que ProblemDetailsService consulta os writers, e este é o último recurso.
        services.AddSingleton<IProblemDetailsWriter, FallbackProblemDetailsWriter>();
        services.AddExceptionHandler<ApplicationValidationExceptionHandler>();
        services.AddExceptionHandler<GlobalExceptionHandler>();
        services.Configure<MvcOptions>(
            options => options.Filters.Add<ProblemDetailsResultFilter>());

        return services;
    }

    internal static void ApplyContract(HttpContext httpContext, ProblemDetails problemDetails)
    {
        var error = ResolveError(httpContext, problemDetails);

        httpContext.Response.StatusCode = error.Status;
        problemDetails.Type = error.Type;
        problemDetails.Title = error.Title;
        problemDetails.Status = error.Status;
        problemDetails.Instance = httpContext.Request.Path.Value;
        problemDetails.Detail = error.Status >= StatusCodes.Status500InternalServerError
            ? null
            : problemDetails.Detail;
        problemDetails.Extensions["code"] = error.Code;
        problemDetails.Extensions["traceId"] = TraceIdentity.Resolve(httpContext);
    }

    private static ApiError ResolveError(HttpContext httpContext, ProblemDetails problemDetails)
    {
        if (problemDetails.Extensions.TryGetValue("code", out var value) &&
            value is string code &&
            ApiErrors.TryGetByCode(code, out var error))
        {
            return error;
        }

        return ApiErrors.ForStatus(problemDetails.Status ?? httpContext.Response.StatusCode);
    }
}
