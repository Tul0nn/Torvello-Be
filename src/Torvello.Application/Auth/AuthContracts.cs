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
    bool MustChangePassword,
    bool TwoFactorEnabled,
    bool TwoFactorSetupRequired);

public sealed record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessTokenExpiresAt,
    [property: System.Text.Json.Serialization.JsonIgnore] string RefreshToken,
    [property: System.Text.Json.Serialization.JsonIgnore] DateTimeOffset RefreshTokenExpiresAt,
    UserDto User);

/// <summary>Who is calling and from where; used for audit records.</summary>
public sealed record RequestContext(string? IpAddress, string? UserAgent);

public interface IAuthService
{
    Task<AuthResponse> LoginTwoFactorAsync(LoginTwoFactorRequest request, RequestContext context, CancellationToken ct);
    Task<TwoFactorStatus> GetTwoFactorAsync(Guid userId);
    Task<TwoFactorSetup> SetupTwoFactorAsync(Guid userId);
    Task<RecoveryCodesResponse> EnableTwoFactorAsync(Guid userId, string code, RequestContext context, CancellationToken ct);
    Task<RecoveryCodesResponse> ResetRecoveryCodesAsync(Guid userId, string code, RequestContext context, CancellationToken ct);
    Task<TwoFactorSessionResponse> DisableTwoFactorAsync(Guid userId, DisableTwoFactorRequest request, RequestContext context, CancellationToken ct);
    Task<object> LoginAsync(LoginRequest request, RequestContext context, CancellationToken ct);
    Task<AuthResponse> RefreshAsync(RefreshRequest request, RequestContext context, CancellationToken ct);
    Task LogoutAsync(LogoutRequest request, RequestContext context, CancellationToken ct);
    Task ForgotPasswordAsync(ForgotPasswordRequest request, RequestContext context, CancellationToken ct);
    Task<AuthResponse> ResetPasswordAsync(ResetPasswordRequest request, RequestContext context, CancellationToken ct);
    Task<AuthResponse> AcceptInviteAsync(AcceptInviteRequest request, RequestContext context, CancellationToken ct);
    Task<AuthResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, RequestContext context, CancellationToken ct);
    Task<UserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct);
}

public sealed record LoginTwoFactorRequest(string TwoFactorToken, string? Code = null, string? RecoveryCode = null);
public sealed record TwoFactorChallenge(bool TwoFactorRequired, string TwoFactorToken);
public sealed record TwoFactorStatus(bool Enabled, int RecoveryCodesLeft);
public sealed record TwoFactorSetup(string SharedKey, string OtpauthUri);
public sealed record TwoFactorCodeRequest(string Code);
public sealed record DisableTwoFactorRequest(string Password, string Code);
public sealed record RecoveryCodesResponse(string[] RecoveryCodes, AuthResponse Session);
public sealed record TwoFactorSessionResponse(AuthResponse Session);
