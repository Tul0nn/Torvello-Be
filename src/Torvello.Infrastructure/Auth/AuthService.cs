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
    TimeProvider clock) : IAuthService
{
    private static readonly AppException WrongCredentials = AppException.Unauthorized("Wrong email or password");

    public async Task<AuthResponse> LoginAsync(LoginRequest request, RequestContext context, CancellationToken ct)
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

        await users.ResetAccessFailedCountAsync(user);
        if (!user.IsActive) throw AppException.Unauthorized("This account has been disabled");
        if (!user.EmailConfirmed) throw AppException.Unauthorized("Accept the invitation in your email first");

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
        if (token is null || token.RevokedAt is not null) return;
        token.RevokedAt = clock.GetUtcNow();
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

    public async Task ResetPasswordAsync(ResetPasswordRequest request, RequestContext context, CancellationToken ct)
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
        await db.SaveChangesAsync(ct);
    }

    public async Task AcceptInviteAsync(AcceptInviteRequest request, RequestContext context, CancellationToken ct)
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
        await db.SaveChangesAsync(ct);
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
        return await users.ToDtoAsync(user);
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

        return new AuthResponse(accessToken, accessExpires, refreshToken, refreshExpires, await users.ToDtoAsync(user));
    }

    internal static async Task RevokeAsync(IQueryable<RefreshToken> query, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var token in await query.Where(t => t.RevokedAt == null).ToListAsync(ct)) token.RevokedAt = now;
    }
}
