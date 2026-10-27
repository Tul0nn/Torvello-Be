namespace Torvello.Application.Auth;

public sealed record LoginRequest(string Email, string Password);

public sealed record RefreshRequest(string RefreshToken);

public sealed record LogoutRequest(string RefreshToken);

public sealed record ForgotPasswordRequest(string Email);

public sealed record ResetPasswordRequest(string Email, string Token, string NewPassword);

public sealed record AcceptInviteRequest(string Email, string Token, string Password);

public sealed record ChangePasswordRequest(string CurrentPassword, string NewPassword);

public sealed record UserDto(
    Guid Id,
    string Email,
    string DisplayName,
    string Role,
    bool IsActive,
    bool EmailConfirmed,
    bool MustChangePassword);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    string RefreshToken,
    DateTimeOffset RefreshTokenExpiresAt,
    UserDto User);

/// <summary>Who is calling and from where; used for audit records.</summary>
public sealed record RequestContext(string? IpAddress, string? UserAgent);

public interface IAuthService
{
    Task<AuthResponse> LoginAsync(LoginRequest request, RequestContext context, CancellationToken ct);
    Task<AuthResponse> RefreshAsync(RefreshRequest request, RequestContext context, CancellationToken ct);
    Task LogoutAsync(LogoutRequest request, RequestContext context, CancellationToken ct);
    Task ForgotPasswordAsync(ForgotPasswordRequest request, RequestContext context, CancellationToken ct);
    Task ResetPasswordAsync(ResetPasswordRequest request, RequestContext context, CancellationToken ct);
    Task AcceptInviteAsync(AcceptInviteRequest request, RequestContext context, CancellationToken ct);
    Task<AuthResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, RequestContext context, CancellationToken ct);
    Task<UserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct);
}
