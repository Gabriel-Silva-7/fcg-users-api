using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace FCG.Users.Api.Errors;

// Os writers embutidos do .NET só aceitam escrever quando o Accept do cliente inclui JSON. O
// caminho de sucesso não faz essa checagem — MVC responde JSON até para quem pede XML —, e a
// assimetria custava caro: com Accept: text/plain o writer recusava, o IExceptionHandler devolvia
// false, e um 400 de validação caía no GlobalExceptionHandler e virava 500. Um header de request
// não pode decidir o status da resposta. Este writer fecha a fila: entra por último, só é
// escolhido quando ninguém mais quis escrever, e emite o mesmo problem+json de sempre.
internal sealed class FallbackProblemDetailsWriter(IOptions<ProblemDetailsOptions> options)
    : IProblemDetailsWriter
{
    private const string ProblemContentType = "application/problem+json";

    public bool CanWrite(ProblemDetailsContext context) => true;

    public ValueTask WriteAsync(ProblemDetailsContext context)
    {
        options.Value.CustomizeProblemDetails?.Invoke(context);

        return new ValueTask(
            context.HttpContext.Response.WriteAsJsonAsync(
                context.ProblemDetails,
                context.ProblemDetails.GetType(),
                options: null,
                contentType: ProblemContentType));
    }
}
