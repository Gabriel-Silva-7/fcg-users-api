using FCG.Users.Application.Common;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Users.Api.Errors;

public sealed class ApplicationValidationExceptionHandler(
    IProblemDetailsService problemDetailsService) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (exception is not ApplicationValidationException validationException)
        {
            return false;
        }

        var error = ApiErrors.ValidationError;
        var problemDetails = new ValidationProblemDetails(
            validationException.Errors.ToDictionary(
                pair => pair.Key,
                pair => pair.Value,
                StringComparer.Ordinal))
        {
            Type = error.Type,
            Title = error.Title,
            Status = error.Status,
            Instance = httpContext.Request.Path,
        };
        problemDetails.Extensions["code"] = error.Code;
        httpContext.Response.StatusCode = error.Status;

        return await problemDetailsService.TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problemDetails,
            });
    }
}
