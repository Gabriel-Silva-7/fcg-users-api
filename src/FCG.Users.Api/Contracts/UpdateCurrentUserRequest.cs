using System.ComponentModel.DataAnnotations;
using FCG.Users.Domain.Identity;

namespace FCG.Users.Api.Contracts;

public sealed record UpdateCurrentUserRequest
{
    [Required]
    [StringLength(User.MaxNameLength, MinimumLength = 1)]
    public string? Name { get; init; }

    [Required]
    [StringLength(FCG.Users.Domain.Identity.Email.MaxLength)]
    public string? Email { get; init; }
}
