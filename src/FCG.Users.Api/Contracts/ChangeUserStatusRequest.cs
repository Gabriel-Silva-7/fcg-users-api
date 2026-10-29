using System.ComponentModel.DataAnnotations;

namespace FCG.Users.Api.Contracts;

public sealed record ChangeUserStatusRequest
{
    public const int XminVersionMaxLength = 10;

    /// <summary>
    /// Whether the account becomes active. Deactivating your own account is rejected.
    /// </summary>
    /// <example>false</example>
    [Required]
    public bool? IsActive { get; init; }

    /// <summary>
    /// Row version of the target user, copied from the <c>version</c> field returned by
    /// GET /api/v1/admin/users (added by the administrative listing increment). It is the PostgreSQL xmin of that row and changes on every update
    /// to the user — including profile and password changes — so read it immediately before this
    /// call. A stale value is rejected with 409 concurrency_conflict.
    /// </summary>
    /// <example>736</example>
    [Required]
    [StringLength(XminVersionMaxLength, MinimumLength = 1)]
    public string? Version { get; init; }
}
