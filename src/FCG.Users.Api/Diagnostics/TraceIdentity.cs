using System.Diagnostics;

namespace FCG.Users.Api.Diagnostics;

internal static class TraceIdentity
{
    // Mantém o mesmo identificador no Problem Details e no log da requisição.
    public static string Resolve(HttpContext httpContext) =>
        Activity.Current?.Id ?? httpContext.TraceIdentifier;
}
