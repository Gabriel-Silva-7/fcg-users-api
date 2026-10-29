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
[Authorize(Policy = IdentityAuthorizationPolicy.AdminOnly)]
[Route("api/v1/admin/users")]
public sealed class AdminUsersController(ChangeUserStatusHandler changeStatus, DeleteUserHandler deleteUser,
    ILogger<AdminUsersController> logger) : ControllerBase
{
    /// <summary>Changes account status using the current opaque version; deletion is terminal.</summary>
    [HttpPatch("{id:guid}/status")]
    [ProducesResponseType<AdminUserResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> ChangeStatus(Guid id, ChangeUserStatusRequest request, CancellationToken cancellationToken)
    {
        var actor = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
        var result = await changeStatus.HandleAsync(new(actor, id, request.IsActive!.Value, request.Version), cancellationToken);
        if (result.Status != ChangeUserStatusStatus.Updated)
        {
            var error = result.Status switch
            {
                ChangeUserStatusStatus.NotFound => ApiErrors.ResourceNotFound,
                ChangeUserStatusStatus.ConcurrencyConflict => ApiErrors.ConcurrencyConflict,
                ChangeUserStatusStatus.CannotDeactivateSelf => ApiErrors.CannotDeactivateSelf,
                ChangeUserStatusStatus.UserDeleted => ApiErrors.UserDeleted,
                _ => throw new InvalidOperationException("Unknown status operation result.")
            };
            return StatusCode(error.Status, error.ToProblemDetails(HttpContext.Request.Path));
        }
        var user = result.User!;
        if (user.IsActive) logger.LogInformation("UserUnblocked {ActorUserId} {TargetUserId} {TraceId}", actor, id, TraceIdentity.Resolve(HttpContext));
        else logger.LogInformation("UserBlocked {ActorUserId} {TargetUserId} {TraceId}", actor, id, TraceIdentity.Resolve(HttpContext));
        return Ok(new AdminUserResponse(user.Id, user.Name, user.Email, user.Role.ToString(), user.IsActive, user.IsDeleted, user.CreatedAtUtc, user.Version));
    }

    /// <summary>Logically deletes another account, retaining identity/history and reserved email. Repetition is a no-op.</summary>
    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status403Forbidden)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status404NotFound)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken cancellationToken)
    {
        var actor = Guid.Parse(User.FindFirstValue(JwtRegisteredClaimNames.Sub)!);
        var result = await deleteUser.HandleAsync(new(actor, id), cancellationToken);
        if (result.Status == DeleteUserStatus.Deleted)
        {
            if (result.Changed) logger.LogInformation("UserDeleted {ActorUserId} {TargetUserId} {TraceId}", actor, id, TraceIdentity.Resolve(HttpContext));
            return NoContent();
        }
        var error = result.Status == DeleteUserStatus.NotFound ? ApiErrors.ResourceNotFound : ApiErrors.CannotDeleteSelf;
        return StatusCode(error.Status, error.ToProblemDetails(HttpContext.Request.Path));
    }
}
