using System.Diagnostics.CodeAnalysis;
using Microsoft.AspNetCore.WebUtilities;

namespace FCG.Users.Api.Errors;

public static class ApiErrors
{
    public static readonly ApiError ValidationError =
        new("validation_error", StatusCodes.Status400BadRequest, "Validation failed");

    public static readonly ApiError Unauthenticated =
        new("unauthenticated", StatusCodes.Status401Unauthorized, "Authentication required");

    public static readonly ApiError InvalidCredentials =
        new("invalid_credentials", StatusCodes.Status401Unauthorized, "Invalid credentials");

    public static readonly ApiError Forbidden =
        new("forbidden", StatusCodes.Status403Forbidden, "Access denied");

    public static readonly ApiError ResourceNotFound =
        new("resource_not_found", StatusCodes.Status404NotFound, "Resource not found");

    public static readonly ApiError EmailAlreadyRegistered =
        new("email_already_registered", StatusCodes.Status409Conflict, "Email already registered");

    public static readonly ApiError InvalidCurrentPassword =
        new("invalid_current_password", StatusCodes.Status400BadRequest, "Invalid current password");

    public static readonly ApiError ConcurrencyConflict =
        new("concurrency_conflict", StatusCodes.Status409Conflict, "Concurrency conflict");

    public static readonly ApiError CannotDeactivateSelf =
        new("cannot_deactivate_self", StatusCodes.Status409Conflict, "Cannot deactivate own account");

    public static readonly ApiError CannotDeleteSelf =
        new("cannot_delete_self", StatusCodes.Status409Conflict, "Cannot delete own account");

    public static readonly ApiError UserDeleted =
        new("user_deleted", StatusCodes.Status409Conflict, "User account is deleted");

    public static readonly ApiError AuthStateUnavailable =
        new("auth_state_unavailable", StatusCodes.Status503ServiceUnavailable, "Authentication state unavailable");

    public static readonly ApiError RateLimitExceeded =
        new("rate_limit_exceeded", StatusCodes.Status429TooManyRequests, "Too many requests");

    public static readonly ApiError InternalError =
        new("internal_error", StatusCodes.Status500InternalServerError, "Unexpected error");

    public static IReadOnlyCollection<ApiError> All { get; } =
    [
        ValidationError,
        Unauthenticated,
        InvalidCredentials,
        Forbidden,
        ResourceNotFound,
        EmailAlreadyRegistered,
        InvalidCurrentPassword,
        ConcurrencyConflict,
        CannotDeactivateSelf,
        CannotDeleteSelf,
        UserDeleted,
        AuthStateUnavailable,
        RateLimitExceeded,
        InternalError,
    ];

    private static readonly Dictionary<string, ApiError> ByCode;

    static ApiErrors() =>
        ByCode = All.ToDictionary(error => error.Code, StringComparer.Ordinal);

    public static bool TryGetByCode(string? code, [NotNullWhen(true)] out ApiError? error)
    {
        if (string.IsNullOrEmpty(code))
        {
            error = null;
            return false;
        }

        return ByCode.TryGetValue(code, out error);
    }

    public static ApiError ForStatus(int status)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(status, StatusCodes.Status400BadRequest);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(status, 599);

        return status switch
        {
            StatusCodes.Status400BadRequest => ValidationError,
            StatusCodes.Status401Unauthorized => Unauthenticated,
            StatusCodes.Status403Forbidden => Forbidden,
            StatusCodes.Status404NotFound => ResourceNotFound,
            StatusCodes.Status429TooManyRequests => RateLimitExceeded,
            StatusCodes.Status500InternalServerError => InternalError,
            _ => new ApiError(
                $"http_{status}",
                status,
                ReasonPhrases.GetReasonPhrase(status) is { Length: > 0 } title ? title : "HTTP error"),
        };
    }
}
