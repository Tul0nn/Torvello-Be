using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Torvello.Api.Infrastructure;
using Torvello.Application.Auth;

namespace Torvello.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting(RateLimits.Auth)]
public sealed class AuthController(IAuthService auth) : ControllerBase
{
    [HttpPost("login")]
    [AllowAnonymous]
    public Task<AuthResponse> Login(LoginRequest request, CancellationToken ct) =>
        auth.LoginAsync(request, HttpContext.ToRequestContext(), ct);

    [HttpPost("refresh")]
    [AllowAnonymous]
    public Task<AuthResponse> Refresh(RefreshRequest request, CancellationToken ct) =>
        auth.RefreshAsync(request, HttpContext.ToRequestContext(), ct);

    [HttpPost("logout")]
    [AllowAnonymous]
    public async Task<IActionResult> Logout(LogoutRequest request, CancellationToken ct)
    {
        await auth.LogoutAsync(request, HttpContext.ToRequestContext(), ct);
        return NoContent();
    }

    /// <summary>Always 202, whether or not the email exists.</summary>
    [HttpPost("forgot-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordRequest request, CancellationToken ct)
    {
        await auth.ForgotPasswordAsync(request, HttpContext.ToRequestContext(), ct);
        return Accepted();
    }

    [HttpPost("reset-password")]
    [AllowAnonymous]
    public async Task<IActionResult> ResetPassword(ResetPasswordRequest request, CancellationToken ct)
    {
        await auth.ResetPasswordAsync(request, HttpContext.ToRequestContext(), ct);
        return NoContent();
    }

    [HttpPost("accept-invite")]
    [AllowAnonymous]
    public async Task<IActionResult> AcceptInvite(AcceptInviteRequest request, CancellationToken ct)
    {
        await auth.AcceptInviteAsync(request, HttpContext.ToRequestContext(), ct);
        return NoContent();
    }

    [HttpPost("change-password")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    public Task<AuthResponse> ChangePassword(ChangePasswordRequest request, CancellationToken ct) =>
        auth.ChangePasswordAsync(User.UserId(), request, HttpContext.ToRequestContext(), ct);

    [HttpGet("me")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    [DisableRateLimiting]
    public Task<UserDto> Me(CancellationToken ct) => auth.GetCurrentUserAsync(User.UserId(), ct);
}
