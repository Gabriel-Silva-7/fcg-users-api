using FCG.Users.Api.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;

namespace FCG.Users.Api.Errors;

public sealed class GlobalExceptionHandler(
    IProblemDetailsService problemDetailsService,
    IHostEnvironment environment,
    ILogger<GlobalExceptionHandler> logger)
    : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
        var traceId = TraceIdentity.Resolve(httpContext);

        if (environment.IsDevelopment())
        {
            // Só em Development, e só no log do servidor: sem isto, um 500 local não dá nenhuma
            // pista além do tipo da exceção, e diagnosticar vira adivinhação. A resposta HTTP
            // continua idêntica — o corpo nunca carrega detalhe, em ambiente nenhum.
            logger.LogError(
                exception,
                "UnhandledException {TraceId} {ExceptionType}",
                traceId,
                exception.GetType().FullName);
        }
        else
        {
            // Fora de Development a exceção não é passada: mensagem e stack trace podem carregar
            // dado sensível para um sink de log que não controlamos.
            logger.LogError(
                "UnhandledException {TraceId} {ExceptionType}",
                traceId,
                exception.GetType().FullName);
        }

        var problemDetails = ApiErrors.InternalError.ToProblemDetails(httpContext.Request.Path);

        return await problemDetailsService.TryWriteAsync(
            new ProblemDetailsContext
            {
                HttpContext = httpContext,
                ProblemDetails = problemDetails,
            });
    }
}
