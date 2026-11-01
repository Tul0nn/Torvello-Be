using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using System.Security.Cryptography;
using System.Text.Json;
using Torvello.Domain;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Torvello.Application.Auth;
using Torvello.Application.Common;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Auth;

public sealed class AuthService(
    AppDbContext db,
    UserManager<AppUser> users,
    TokenService tokens,
    AuditLog audit,
    IEmailSender email,
    IOptions<JwtOptions> jwtOptions,
    IOptions<AppLinkOptions> links,
    TimeProvider clock,
    IDataProtectionProvider protection,
    IConfiguration configuration) : IAuthService
{
    private static readonly AppException WrongCredentials = AppException.Unauthorized("Wrong email or password");

    public async Task<object> LoginAsync(LoginRequest request, RequestContext context, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null)
        {
            // Hash anyway so an unknown email takes as long as a wrong password.
            users.PasswordHasher.HashPassword(new AppUser(), request.Password);
            audit.Add("sign_in_failed", null, context, new { reason = "unknown_email" });
            await db.SaveChangesAsync(ct);
            throw WrongCredentials;
        }

        if (await users.IsLockedOutAsync(user))
        {
            audit.Add("sign_in_blocked", user.Id, context);
            await db.SaveChangesAsync(ct);
            throw AppException.Unauthorized("Too many wrong passwords. Try again in a few minutes.");
        }

        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            await users.AccessFailedAsync(user);
            audit.Add("sign_in_failed", user.Id, context, new { reason = "wrong_password" });
            await db.SaveChangesAsync(ct);
            throw WrongCredentials;
        }


        if (!user.IsActive) throw AppException.Unauthorized("This account has been disabled");
        if (!user.EmailConfirmed) throw AppException.Unauthorized("Accept the invitation in your email first");

        if (user.TwoFactorEnabled)
        {
            var protector = protection.CreateProtector("Torvello.Login2fa.v1").ToTimeLimitedDataProtector();
            return new TwoFactorChallenge(true, protector.Protect(
                JsonSerializer.Serialize(new LoginPayload(user.Id, user.SecurityStamp ?? "")), TimeSpan.FromMinutes(5)));
        }
        await users.ResetAccessFailedCountAsync(user);
        audit.Add("sign_in", user.Id, context);
        return await IssueAsync(user, Guid.NewGuid(), context, ct);
    }

    public async Task<AuthResponse> RefreshAsync(RefreshRequest request, RequestContext context, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var hash = TokenService.Hash(request.RefreshToken);
        var token = await db.RefreshTokens.Include(t => t.User).SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token?.User is null) throw AppException.Unauthorized("Please sign in again");

        if (token.RevokedAt is not null)
        {
            // An already-used token came back: it may have been stolen. End every session in this family.
            await RevokeAsync(db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId), now, ct);
            audit.Add("refresh_token_reuse", token.UserId, context);
            await db.SaveChangesAsync(ct);
            throw AppException.Unauthorized("Please sign in again");
        }

        var user = token.User;
        if (token.ExpiresAt <= now || !user.IsActive || await users.IsLockedOutAsync(user))
        {
            throw AppException.Unauthorized("Please sign in again");
        }

        var response = await IssueAsync(user, token.FamilyId, context, ct, saveChanges: false);
        token.RevokedAt = now;
        token.ReplacedByHash = TokenService.Hash(response.RefreshToken);
        await db.SaveChangesAsync(ct);
        return response;
    }

    public async Task LogoutAsync(LogoutRequest request, RequestContext context, CancellationToken ct)
    {
        var hash = TokenService.Hash(request.RefreshToken);
        var token = await db.RefreshTokens.SingleOrDefaultAsync(t => t.TokenHash == hash, ct);
        if (token is null) return;
        await RevokeAsync(db.RefreshTokens.Where(t => t.FamilyId == token.FamilyId), clock.GetUtcNow(), ct);
        audit.Add("sign_out", token.UserId, context);
        await db.SaveChangesAsync(ct);
    }

    public async Task ForgotPasswordAsync(ForgotPasswordRequest request, RequestContext context, CancellationToken ct)
    {
        // Same response whether or not the email exists, so this cannot be used to find accounts.
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive || !user.EmailConfirmed) return;

        var token = UserMapping.EncodeToken(await users.GeneratePasswordResetTokenAsync(user));
        var link = $"{links.Value.WebBaseUrl.TrimEnd('/')}/reset-password?email={Uri.EscapeDataString(user.Email!)}&token={token}";
        await email.SendAsync(new EmailMessage(
            user.Email!,
            "Reset your Torvello password",
            $"Hi {user.DisplayName},\n\nUse this link to choose a new password:\n{link}\n\nIf you did not ask for this, you can ignore this email."), ct);
        audit.Add("password_reset_requested", user.Id, context);
        await db.SaveChangesAsync(ct);
    }

    public async Task<AuthResponse> ResetPasswordAsync(ResetPasswordRequest request, RequestContext context, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive) throw AppException.BadRequest("This link is invalid or has expired");

        var result = await users.ResetPasswordAsync(user, UserMapping.DecodeToken(request.Token), request.NewPassword);
        if (!result.Succeeded)
        {
            throw result.Errors.Any(e => e.Code == "InvalidToken")
                ? AppException.BadRequest("This link is invalid or has expired")
                : result.ToAppException();
        }

        // ResetPasswordAsync rotates the security stamp, which already invalidates access tokens.
        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        await users.SetLockoutEndDateAsync(user, null);
        await RevokeAsync(db.RefreshTokens.Where(t => t.UserId == user.Id), clock.GetUtcNow(), ct);
        audit.Add("password_reset", user.Id, context);
        return await IssueAsync(user, Guid.NewGuid(), context, ct);
    }

    public async Task<AuthResponse> AcceptInviteAsync(AcceptInviteRequest request, RequestContext context, CancellationToken ct)
    {
        var user = await users.FindByEmailAsync(request.Email.Trim());
        if (user is null || !user.IsActive) throw AppException.BadRequest("This invitation is invalid or has expired");
        if (user.EmailConfirmed || await users.HasPasswordAsync(user)) throw AppException.BadRequest("This invitation has already been used. Sign in instead.");

        // Check the password before using the token, so a rejected password leaves the invitation usable.
        foreach (var validator in users.PasswordValidators)
        {
            var check = await validator.ValidateAsync(users, user, request.Password);
            if (!check.Succeeded) throw check.ToAppException();
        }

        var confirmed = await users.ConfirmEmailAsync(user, UserMapping.DecodeToken(request.Token));
        if (!confirmed.Succeeded) throw AppException.BadRequest("This invitation is invalid or has expired");

        var added = await users.AddPasswordAsync(user, request.Password);
        if (!added.Succeeded) throw added.ToAppException();

        audit.Add("invite_accepted", user.Id, context);
        return await IssueAsync(user, Guid.NewGuid(), context, ct);
    }

    public async Task<AuthResponse> ChangePasswordAsync(Guid userId, ChangePasswordRequest request, RequestContext context, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.Unauthorized("Please sign in again");
        var result = await users.ChangePasswordAsync(user, request.CurrentPassword, request.NewPassword);
        if (!result.Succeeded)
        {
            throw result.Errors.Any(e => e.Code == "PasswordMismatch")
                ? AppException.BadRequest("Current password is wrong")
                : result.ToAppException();
        }

        user.MustChangePassword = false;
        await users.UpdateAsync(user);
        // Sign out every other device; this one gets fresh tokens below.
        await RevokeAsync(db.RefreshTokens.Where(t => t.UserId == user.Id), clock.GetUtcNow(), ct);
        audit.Add("password_changed", user.Id, context);
        return await IssueAsync(user, Guid.NewGuid(), context, ct);
    }

    public async Task<UserDto> GetCurrentUserAsync(Guid userId, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(userId.ToString()) ?? throw AppException.Unauthorized("Please sign in again");
        return await DtoAsync(user);
    }

    private async Task<AuthResponse> IssueAsync(AppUser user, Guid familyId, RequestContext context, CancellationToken ct, bool saveChanges = true)
    {
        var now = clock.GetUtcNow();
        var role = await users.RoleOfAsync(user);
        var (accessToken, accessExpires) = tokens.CreateAccessToken(user, role);
        var refreshToken = TokenService.NewRefreshToken();
        var refreshExpires = now.AddDays(jwtOptions.Value.RefreshTokenDays);

        db.RefreshTokens.Add(new RefreshToken
        {
            UserId = user.Id,
            TokenHash = TokenService.Hash(refreshToken),
            FamilyId = familyId,
            CreatedAt = now,
            ExpiresAt = refreshExpires,
            CreatedByIp = context.IpAddress,
        });
        if (saveChanges) await db.SaveChangesAsync(ct);

        return new AuthResponse(accessToken, accessExpires, refreshToken, refreshExpires, await DtoAsync(user));
    }

    private sealed record LoginPayload(Guid UserId, string Stamp);

    private async Task<UserDto> DtoAsync(AppUser user)
    {
        return await users.ToDtoAsync(user, configuration.GetValue("Security:RequireTwoFactorForManagers", true));
    }

    private async Task<AppUser> FindAsync(Guid id) =>
        await users.FindByIdAsync(id.ToString()) ?? throw AppException.Unauthorized("Please sign in again");

    private static string Normalize(string? code) => (code ?? "").Replace(" ", "").Replace("-", "");
    private static void Check(IdentityResult result)
    {
        if (!result.Succeeded) throw result.ToAppException();
    }

    private async Task VerifyAsync(AppUser user, string? code, RequestContext context, CancellationToken ct)
    {
        if (await users.IsLockedOutAsync(user)) throw AppException.Unauthorized("Too many wrong codes. Try again in a few minutes.");
        if (!await users.VerifyTwoFactorTokenAsync(user, TokenOptions.DefaultAuthenticatorProvider, Normalize(code)))
        {
            Check(await users.AccessFailedAsync(user));
            audit.Add("login_2fa_failed", user.Id, context);
            await db.SaveChangesAsync(ct);
            throw AppException.Unauthorized("Invalid two-step sign-in code");
        }
        Check(await users.ResetAccessFailedCountAsync(user));
    }

    public async Task<AuthResponse> LoginTwoFactorAsync(LoginTwoFactorRequest request, RequestContext context, CancellationToken ct)
    {
        LoginPayload payload;
        try
        {
            var value = protection.CreateProtector("Torvello.Login2fa.v1").ToTimeLimitedDataProtector()
                .Unprotect(request.TwoFactorToken, out _);
            payload = JsonSerializer.Deserialize<LoginPayload>(value) ?? throw new CryptographicException();
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or ArgumentException or FormatException)
        {
            throw AppException.Unauthorized("Two-step sign-in expired. Sign in again.");
        }
        var user = await FindAsync(payload.UserId);
        if (!user.IsActive || !user.EmailConfirmed || !user.TwoFactorEnabled || user.SecurityStamp != payload.Stamp ||
            await users.IsLockedOutAsync(user)) throw AppException.Unauthorized("Please sign in again");
        if (string.IsNullOrWhiteSpace(request.RecoveryCode))
            await VerifyAsync(user, request.Code, context, ct);
        else
        {
            var recovery = Normalize(request.RecoveryCode);
            if (recovery.Length == 10) recovery = recovery.Insert(5, "-");
            var result = await users.RedeemTwoFactorRecoveryCodeAsync(user, recovery);
            if (!result.Succeeded)
            {
                Check(await users.AccessFailedAsync(user));
                audit.Add("login_2fa_failed", user.Id, context);
                await db.SaveChangesAsync(ct);
                throw AppException.Unauthorized("Invalid recovery code");
            }
            Check(await users.ResetAccessFailedCountAsync(user));
            audit.Add("2fa_recovery_code_used", user.Id, context);
        }
        audit.Add("sign_in", user.Id, context);
        return await IssueAsync(user, Guid.NewGuid(), context, ct);
    }

    public async Task<TwoFactorStatus> GetTwoFactorAsync(Guid userId)
    {
        var user = await FindAsync(userId);
        return new(user.TwoFactorEnabled, await users.CountRecoveryCodesAsync(user));
    }

    public async Task<TwoFactorSetup> SetupTwoFactorAsync(Guid userId)
    {
        var user = await FindAsync(userId);
        if (user.TwoFactorEnabled) throw AppException.BadRequest("Disable two-step sign-in before setting up a new key");
        // Identity rotates the stamp when resetting a key. An unenrolled user must
        // retain this session to verify the new key; enable performs the session revocation.
        var stamp = user.SecurityStamp;
        Check(await users.ResetAuthenticatorKeyAsync(user));
        user.SecurityStamp = stamp;
        Check(await users.UpdateAsync(user));
        var key = await users.GetAuthenticatorKeyAsync(user) ?? throw AppException.BadRequest("Could not create authenticator key");
        var formatted = string.Join(" ", Enumerable.Range(0, (key.Length + 3) / 4)
            .Select(i => key.Substring(i * 4, Math.Min(4, key.Length - i * 4))));
        return new(formatted, $"otpauth://totp/Torvello:{Uri.EscapeDataString(user.Email!)}?secret={key}&issuer=Torvello&digits=6");
    }

    private async Task<AuthResponse> ChangedAsync(AppUser user, string action, RequestContext context, CancellationToken ct)
    {
        Check(await users.UpdateSecurityStampAsync(user));
        await RevokeAsync(db.RefreshTokens.Where(t => t.UserId == user.Id), clock.GetUtcNow(), ct);
        audit.Add(action, user.Id, context);
        return await IssueAsync(user, Guid.NewGuid(), context, ct);
    }

    public async Task<RecoveryCodesResponse> EnableTwoFactorAsync(Guid userId, string code, RequestContext context, CancellationToken ct)
    {
        var user = await FindAsync(userId);
        if (user.TwoFactorEnabled) throw AppException.BadRequest("Two-step sign-in is already enabled");
        await VerifyAsync(user, code, context, ct);
        Check(await users.SetTwoFactorEnabledAsync(user, true));
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        var session = await ChangedAsync(user, "2fa_enabled", context, ct);
        return new(codes?.ToArray() ?? throw AppException.BadRequest("Could not generate recovery codes"), session);
    }

    public async Task<RecoveryCodesResponse> ResetRecoveryCodesAsync(Guid userId, string code, RequestContext context, CancellationToken ct)
    {
        var user = await FindAsync(userId);
        if (!user.TwoFactorEnabled) throw AppException.BadRequest("Enable two-step sign-in first");
        await VerifyAsync(user, code, context, ct);
        var codes = await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 10);
        var session = await ChangedAsync(user, "2fa_recovery_codes_reset", context, ct);
        return new(codes?.ToArray() ?? throw AppException.BadRequest("Could not generate recovery codes"), session);
    }

    public async Task<TwoFactorSessionResponse> DisableTwoFactorAsync(Guid userId, DisableTwoFactorRequest request, RequestContext context, CancellationToken ct)
    {
        var user = await FindAsync(userId);
        if (configuration.GetValue("Security:RequireTwoFactorForManagers", true) &&
            await users.RoleOfAsync(user) is Roles.Owner or Roles.Manager)
            throw AppException.BadRequest("Owners and managers must keep two-step sign-in enabled while the security policy is on");
        if (await users.IsLockedOutAsync(user)) throw AppException.Unauthorized("Please try again in a few minutes");
        if (!await users.CheckPasswordAsync(user, request.Password))
        {
            Check(await users.AccessFailedAsync(user));
            throw AppException.BadRequest("Current password is wrong");
        }
        await VerifyAsync(user, request.Code, context, ct);
        Check(await users.SetTwoFactorEnabledAsync(user, false));
        Check(await users.ResetAuthenticatorKeyAsync(user));
        await users.GenerateNewTwoFactorRecoveryCodesAsync(user, 0);
        return new(await ChangedAsync(user, "2fa_disabled", context, ct));
    }

    internal static async Task RevokeAsync(IQueryable<RefreshToken> query, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var token in await query.Where(t => t.RevokedAt == null).ToListAsync(ct)) token.RevokedAt = now;
    }
}
