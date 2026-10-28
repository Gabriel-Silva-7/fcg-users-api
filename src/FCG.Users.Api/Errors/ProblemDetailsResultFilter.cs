using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Filters;

namespace FCG.Users.Api.Errors;

public sealed class ProblemDetailsResultFilter : IAsyncResultFilter
{
    public async Task OnResultExecutionAsync(
        ResultExecutingContext context,
        ResultExecutionDelegate next)
    {
        if (context.Result is ObjectResult { Value: ProblemDetails problemDetails } objectResult &&
            (problemDetails.Status ?? objectResult.StatusCode ?? context.HttpContext.Response.StatusCode) >=
            StatusCodes.Status400BadRequest)
        {
            ProblemDetailsConfiguration.ApplyContract(context.HttpContext, problemDetails);
            objectResult.StatusCode = problemDetails.Status;
        }

        await next();
    }
}
