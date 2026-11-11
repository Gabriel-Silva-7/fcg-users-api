using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using FCG.Contracts;
using FCG.Users.Api.Contracts;
using FCG.Users.Api.Diagnostics;
using FCG.Users.Api.Errors;
using FCG.Users.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Users.Api.Controllers;

[ApiController]
[Authorize(Policy = IdentityAuthorizationPolicy.UserOrAdmin)]
[Route("api/v1/me")]
public sealed class MeController(GetCurrentUserHandler getCurrent, UpdateCurrentUserHandler updateCurrent, ChangeOwnPasswordHandler changePassword, ILogger<MeController> logger) : ControllerBase
{
    /// <summary>Returns only the authenticated account's own profile.</summary>
    [HttpGet]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Get(CancellationToken cancellationToken)
    {
        var user = await getCurrent.HandleAsync(Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!), cancellationToken);
        return user is null ? Unauthorized(ApiErrors.Unauthenticated.ToProblemDetails(HttpContext.Request.Path)) : Ok(ToResponse(user));
    }

    /// <summary>Updates the authenticated account's name/email; losing an access-state race returns401, another edit409.</summary>
    [HttpPatch]
    [ProducesResponseType<CurrentUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Update(UpdateCurrentUserRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
        var result = await updateCurrent.HandleAsync(new(id, request.Name, request.Email), cancellationToken);
        if (result.Status == UpdateCurrentUserStatus.Updated)
        {
            logger.LogInformation("UserProfileUpdated {ActorUserId} {TraceId}", id, TraceIdentity.Resolve(HttpContext));
            return Ok(ToResponse(result.User!));
        }
        var error = result.Status switch
        {
            UpdateCurrentUserStatus.NotFound => ApiErrors.Unauthenticated,
            UpdateCurrentUserStatus.EmailAlreadyRegistered => ApiErrors.EmailAlreadyRegistered,
            UpdateCurrentUserStatus.ConcurrencyConflict => ApiErrors.ConcurrencyConflict,
            _ => throw new InvalidOperationException("Unknown profile operation result.")
        };
        return StatusCode(error.Status, error.ToProblemDetails(HttpContext.Request.Path));
    }
    /// <summary>Changes only the authenticated account's password; already-issued JWTs retain their existing lifetime.</summary>
    [HttpPatch("password")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ChangePassword(ChangeOwnPasswordRequest request, CancellationToken cancellationToken)
    {
        var id = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
        var result = await changePassword.HandleAsync(new(id, request.CurrentPassword, request.NewPassword), cancellationToken);
        if (result.Status == ChangeOwnPasswordStatus.Updated)
        {
            logger.LogInformation("UserPasswordChanged {ActorUserId} {TraceId}", id, TraceIdentity.Resolve(HttpContext));
            return NoContent();
        }
        var error = result.Status switch
        {
            ChangeOwnPasswordStatus.NotFound => ApiErrors.Unauthenticated,
            ChangeOwnPasswordStatus.InvalidCurrentPassword => ApiErrors.InvalidCurrentPassword,
            ChangeOwnPasswordStatus.ConcurrencyConflict => ApiErrors.ConcurrencyConflict,
            _ => throw new InvalidOperationException("Unknown password operation result.")
        };
        return StatusCode(error.Status, error.ToProblemDetails(HttpContext.Request.Path));
    }
    private static CurrentUserResponse ToResponse(CurrentUser user) => new(user.Id, user.Name, user.Email, user.Role.ToString());
}
