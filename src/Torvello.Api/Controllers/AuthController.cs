using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Torvello.Api.Infrastructure;
using Torvello.Application.Auth;

namespace Torvello.Api.Controllers;

[ApiController]
[Route("api/auth")]
[EnableRateLimiting(RateLimits.Auth)]
public sealed class AuthController(IAuthService auth, IWebHostEnvironment environment) : ControllerBase
{
    private const string CookieName = "torvello_rt";
    private CookieOptions CookieOptions(DateTimeOffset? expires = null) => new()
    {
        HttpOnly = true,
        SameSite = SameSiteMode.Strict,
        Path = "/api/auth",
        Secure = !environment.IsDevelopment() || Request.IsHttps,
        Expires = expires,
    };

    private AuthResponse SetCookie(AuthResponse response)
    {
        Response.Cookies.Append(CookieName, response.RefreshToken, CookieOptions(response.RefreshTokenExpiresAt));
        return response;
    }

    private void RequireClient()
    {
        if (Request.Headers["X-Torvello-Client"] != "web")
            throw Torvello.Application.Common.AppException.BadRequest("X-Torvello-Client: web is required");
    }

    [HttpPost("login")]
    [AllowAnonymous]
    public async Task<object> Login(LoginRequest request, CancellationToken ct)
    {
        var result = await auth.LoginAsync(request, HttpContext.ToRequestContext(), ct);
        return result is AuthResponse response ? SetCookie(response) : result;
    }

    [HttpPost("login/2fa")]
    [AllowAnonymous]
    public async Task<AuthResponse> LoginTwoFactor(LoginTwoFactorRequest request, CancellationToken ct) =>
        SetCookie(await auth.LoginTwoFactorAsync(request, HttpContext.ToRequestContext(), ct));

    [HttpPost("refresh")]
    [AllowAnonymous]
    [AllowWhilePasswordChangeRequired]
    public async Task<AuthResponse> Refresh(CancellationToken ct)
    {
        RequireClient();
        var token = Request.Cookies[CookieName] ?? throw Torvello.Application.Common.AppException.Unauthorized("Please sign in again");
        return SetCookie(await auth.RefreshAsync(new RefreshRequest(token), HttpContext.ToRequestContext(), ct));
    }

    [HttpPost("logout")]
    [AllowAnonymous]
    [AllowWhilePasswordChangeRequired]
    public async Task<IActionResult> Logout(CancellationToken ct)
    {
        RequireClient();
        var token = Request.Cookies[CookieName];
        if (token is not null)
            await auth.LogoutAsync(new LogoutRequest(token), HttpContext.ToRequestContext(), ct);
        Response.Cookies.Delete(CookieName, CookieOptions());
        return NoContent();
    }

    [HttpGet("2fa")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    public Task<TwoFactorStatus> TwoFactor() => auth.GetTwoFactorAsync(User.UserId());

    [HttpPost("2fa/setup")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    public Task<TwoFactorSetup> SetupTwoFactor() => auth.SetupTwoFactorAsync(User.UserId());

    [HttpPost("2fa/enable")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    public async Task<RecoveryCodesResponse> EnableTwoFactor(TwoFactorCodeRequest request, CancellationToken ct)
    {
        var response = await auth.EnableTwoFactorAsync(User.UserId(), request.Code, HttpContext.ToRequestContext(), ct);
        SetCookie(response.Session);
        return response;
    }

    [HttpPost("2fa/recovery-codes")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    public async Task<RecoveryCodesResponse> RecoveryCodes(TwoFactorCodeRequest request, CancellationToken ct)
    {
        var response = await auth.ResetRecoveryCodesAsync(User.UserId(), request.Code, HttpContext.ToRequestContext(), ct);
        SetCookie(response.Session);
        return response;
    }

    [HttpPost("2fa/disable")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    public async Task<TwoFactorSessionResponse> DisableTwoFactor(DisableTwoFactorRequest request, CancellationToken ct)
    {
        var response = await auth.DisableTwoFactorAsync(User.UserId(), request, HttpContext.ToRequestContext(), ct);
        SetCookie(response.Session);
        return response;
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
    public async Task<AuthResponse> ResetPassword(ResetPasswordRequest request, CancellationToken ct) =>
        SetCookie(await auth.ResetPasswordAsync(request, HttpContext.ToRequestContext(), ct));

    [HttpPost("accept-invite")]
    [AllowAnonymous]
    public async Task<AuthResponse> AcceptInvite(AcceptInviteRequest request, CancellationToken ct) =>
        SetCookie(await auth.AcceptInviteAsync(request, HttpContext.ToRequestContext(), ct));

    [HttpPost("change-password")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    public async Task<AuthResponse> ChangePassword(ChangePasswordRequest request, CancellationToken ct) =>
        SetCookie(await auth.ChangePasswordAsync(User.UserId(), request, HttpContext.ToRequestContext(), ct));

    [HttpGet("me")]
    [Authorize]
    [AllowWhilePasswordChangeRequired]
    [DisableRateLimiting]
    public Task<UserDto> Me(CancellationToken ct) => auth.GetCurrentUserAsync(User.UserId(), ct);
}
