using FCG.Users.Api.Diagnostics;
using FCG.Users.Api.Contracts;
using FCG.Users.Api.Errors;
using FCG.Users.Api.Security;
using FCG.Users.Application.Common;
using FCG.Users.Application.Identity;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Mvc;

namespace FCG.Users.Api.Controllers;

[ApiController]
[AllowAnonymous]
[Route("api/v1/auth")]
public sealed class AuthController(
    RegisterUserHandler registerUserHandler,
    LoginUserHandler loginUserHandler,
    ILogger<AuthController> logger) : ControllerBase
{
    /// <summary>
    /// Registers a new user account.
    /// </summary>
    [HttpPost("register")]
    [EnableRateLimiting(RateLimitingConfiguration.RegisterPolicy)]
    [ProducesResponseType<UserResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status409Conflict)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Register(
        RegisterRequest request,
        CancellationToken cancellationToken)
    {
        var result = await registerUserHandler.HandleAsync(
            new RegisterUserCommand(request.Name, request.Email, request.Password),
            cancellationToken);

        if (result.Status is not RegisterUserStatus.Created)
        {
            return result.Status switch
            {
                RegisterUserStatus.EmailAlreadyRegistered => Conflict(
                    ApiErrors.EmailAlreadyRegistered.ToProblemDetails(HttpContext.Request.Path)),
                _ => throw new InvalidOperationException(
                    $"Unexpected registration result: {result.Status}."),
            };
        }

        var user = result.User!;
        var response = new UserResponse(
            user.Id,
            user.Name,
            user.Email,
            user.Role.ToString());

        logger.LogInformation(
            "UserRegistered {TargetUserId} {MaskedEmail} {TraceId}",
            user.Id,
            SensitiveDataMasker.MaskEmail(user.Email),
            TraceIdentity.Resolve(HttpContext));

        return Created("/api/v1/me", response);
    }

    /// <summary>
    /// Authenticates a user and issues an access token.
    /// </summary>
    [HttpPost("login")]
    [EnableRateLimiting(RateLimitingConfiguration.LoginPolicy)]
    [ProducesResponseType<TokenResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType<ValidationProblemDetails>(StatusCodes.Status400BadRequest)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status401Unauthorized)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType<ProblemDetails>(StatusCodes.Status503ServiceUnavailable)]
    public async Task<IActionResult> Login(
        LoginRequest request,
        CancellationToken cancellationToken)
    {
        var result = await loginUserHandler.HandleAsync(
            new LoginUserCommand(request.Email, request.Password),
            cancellationToken);

        if (result.Status is LoginUserStatus.StateUnavailable)
        {
            Response.Headers.RetryAfter = "1";
            logger.LogWarning("LoginStateUnavailable {TraceId}", TraceIdentity.Resolve(HttpContext));
            return StatusCode(StatusCodes.Status503ServiceUnavailable, ApiErrors.AuthStateUnavailable.ToProblemDetails(HttpContext.Request.Path));
        }

        if (result.Status is LoginUserStatus.InvalidCredentials)
        {
            logger.LogWarning(
                "LoginFailed {MaskedEmail} {RemoteIpAddress} {TraceId}",
                SensitiveDataMasker.MaskEmail(request.Email),
                RateLimitingConfiguration.ResolvePartitionKey(HttpContext),
                TraceIdentity.Resolve(HttpContext));

            return Unauthorized(
                ApiErrors.InvalidCredentials.ToProblemDetails(HttpContext.Request.Path));
        }

        var token = result.Token!;
        return Ok(new TokenResponse(token.Value, "Bearer", token.ExpiresInSeconds));
    }
}
