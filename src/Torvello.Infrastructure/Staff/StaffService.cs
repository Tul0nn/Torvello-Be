using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Torvello.Application.Auth;
using Torvello.Application.Common;
using Torvello.Application.Staff;
using Torvello.Domain;
using Torvello.Infrastructure.Auth;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Staff;

public sealed class StaffService(
    AppDbContext db,
    UserManager<AppUser> users,
    AuditLog audit,
    IEmailSender email,
    IOptions<AppLinkOptions> links,
    TimeProvider clock) : IStaffService
{
    public async Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct)
    {
        var all = await users.Users.OrderByDescending(u => u.IsActive).ThenBy(u => u.DisplayName).ToListAsync(ct);
        var result = new List<UserDto>(all.Count);
        foreach (var user in all) result.Add(await users.ToDtoAsync(user));
        return result;
    }

    public async Task<UserDto> InviteAsync(Guid actorId, InviteStaffRequest request, RequestContext context, CancellationToken ct)
    {
        var emailAddress = request.Email.Trim();
        if (await users.FindByEmailAsync(emailAddress) is not null) throw AppException.Conflict("Another staff member already uses that email");

        var user = new AppUser
        {
            Id = Guid.NewGuid(),
            UserName = emailAddress,
            Email = emailAddress,
            DisplayName = request.DisplayName.Trim(),
            CreatedAt = clock.GetUtcNow(),
        };
        // No password yet: the invitation link lets them choose one, which also confirms the email.
        var created = await users.CreateAsync(user);
        if (!created.Succeeded) throw created.ToAppException();
        await users.AddToRoleAsync(user, request.Role);

        await SendInviteAsync(user);
        audit.Add("staff_invited", actorId, context, new { staffId = user.Id, role = request.Role });
        await db.SaveChangesAsync(ct);
        return await users.ToDtoAsync(user);
    }

    public async Task<UserDto> UpdateAsync(Guid actorId, Guid staffId, UpdateStaffRequest request, RequestContext context, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(staffId.ToString()) ?? throw AppException.NotFound("Staff member not found");
        var currentRole = await users.RoleOfAsync(user);

        if (staffId == actorId && (!request.IsActive || request.Role != Roles.Owner))
        {
            throw AppException.BadRequest("You cannot remove your own owner access");
        }
        var losingOwner = currentRole == Roles.Owner && (request.Role != Roles.Owner || !request.IsActive);
        if (losingOwner && await CountActiveOwnersAsync() <= 1)
        {
            throw AppException.BadRequest("At least one active owner is required");
        }

        var accessChanged = currentRole != request.Role || user.IsActive != request.IsActive;
        user.DisplayName = request.DisplayName.Trim();
        user.IsActive = request.IsActive;
        var updated = await users.UpdateAsync(user);
        if (!updated.Succeeded) throw updated.ToAppException();

        if (currentRole != request.Role)
        {
            await users.RemoveFromRolesAsync(user, await users.GetRolesAsync(user));
            await users.AddToRoleAsync(user, request.Role);
        }
        if (accessChanged)
        {
            // Old access tokens carry the old role; make them invalid now rather than when they expire.
            await users.UpdateSecurityStampAsync(user);
            await AuthService.RevokeAsync(db.RefreshTokens.Where(t => t.UserId == user.Id), clock.GetUtcNow(), ct);
        }

        audit.Add("staff_updated", actorId, context, new { staffId, role = request.Role, request.IsActive });
        await db.SaveChangesAsync(ct);
        return await users.ToDtoAsync(user);
    }

    public async Task ResendInviteAsync(Guid actorId, Guid staffId, RequestContext context, CancellationToken ct)
    {
        var user = await users.FindByIdAsync(staffId.ToString()) ?? throw AppException.NotFound("Staff member not found");
        if (user.EmailConfirmed) throw AppException.BadRequest("This person has already accepted their invitation");
        if (!user.IsActive) throw AppException.BadRequest("Enable this account before sending an invitation");
        await SendInviteAsync(user);
        audit.Add("staff_invite_resent", actorId, context, new { staffId });
        await db.SaveChangesAsync(ct);
    }

    private async Task SendInviteAsync(AppUser user)
    {
        var token = UserMapping.EncodeToken(await users.GenerateEmailConfirmationTokenAsync(user));
        var link = $"{links.Value.WebBaseUrl.TrimEnd('/')}/accept-invite?email={Uri.EscapeDataString(user.Email!)}&token={token}";
        await email.SendAsync(new EmailMessage(
            user.Email!,
            "You're invited to Torvello",
            $"Hi {user.DisplayName},\n\nYou have been added to the Torvello team. Open this link to choose your password:\n{link}\n\nThe link expires in 3 days."));
    }

    private async Task<int> CountActiveOwnersAsync()
    {
        var owners = await users.GetUsersInRoleAsync(Roles.Owner);
        return owners.Count(o => o.IsActive);
    }
}
