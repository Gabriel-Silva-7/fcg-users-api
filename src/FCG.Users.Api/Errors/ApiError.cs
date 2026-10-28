using Microsoft.AspNetCore.Mvc;

namespace FCG.Users.Api.Errors;

public sealed record ApiError(string Code, int Status, string Title)
{
    public const string TypePrefix = "urn:fcg:error:";

    public string Type => TypePrefix + Code.Replace('_', '-');

    public ProblemDetails ToProblemDetails(string instance, string? detail = null) =>
        new()
        {
            Type = Type,
            Title = Title,
            Status = Status,
            Instance = instance,
            Detail = detail,
            Extensions = { ["code"] = Code },
        };
}
