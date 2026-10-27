using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Torvello.Application.Auth;
using Torvello.Application.Common;
using Torvello.Application.Hr;
using Torvello.Infrastructure.Auth;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Hr;

public sealed class PolicyService(AppDbContext db, UserManager<AppUser> users, AuditLog audit, TimeProvider clock) : IPolicyService
{
    private static readonly RequestContext NoContext = new(null, null);

    public async Task<IReadOnlyList<PolicyDto>> ListAsync(Guid userId, bool canManage, CancellationToken ct)
    {
        var policies = await db.Policies.AsNoTracking()
            .Where(p => !p.Deleted && (canManage || p.Published))
            .ToListAsync(ct);
        var acks = await db.PolicyAcknowledgements.AsNoTracking()
            .Where(a => a.UserId == userId)
            .ToDictionaryAsync(a => a.PolicyId, ct);
        var names = await EditorNamesAsync(policies.Select(p => p.UpdatedBy), ct);
        return policies
            .OrderBy(p => p.Category, StringComparer.OrdinalIgnoreCase)
            .ThenBy(p => p.Title, StringComparer.OrdinalIgnoreCase)
            .Select(p => ToDto(p, names, acks.GetValueOrDefault(p.Id)))
            .ToList();
    }

    public async Task<PolicyDto> GetAsync(Guid userId, bool canManage, Guid policyId, CancellationToken ct)
    {
        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == policyId && !p.Deleted, ct);
        if (policy is null || (!policy.Published && !canManage)) throw AppException.NotFound("Policy not found");
        var ack = await db.PolicyAcknowledgements.AsNoTracking().SingleOrDefaultAsync(a => a.PolicyId == policyId && a.UserId == userId, ct);
        return ToDto(policy, await EditorNamesAsync([policy.UpdatedBy], ct), ack);
    }

    public async Task<PolicyDto> CreateAsync(Guid actorId, PolicyInput input, CancellationToken ct)
    {
        var policy = new Policy
        {
            Title = input.Title.Trim(),
            Category = input.Category.Trim(),
            Body = input.Body.Trim(),
            Published = input.Published,
            UpdatedAt = clock.GetUtcNow(),
            UpdatedBy = actorId,
        };
        db.Policies.Add(policy);
        audit.Add("policy_created", actorId, NoContext, new { policyId = policy.Id, policy.Title, policy.Published });
        await db.SaveChangesAsync(ct);
        return ToDto(policy, await EditorNamesAsync([actorId], ct), null);
    }

    public async Task<PolicyDto> UpdateAsync(Guid actorId, Guid policyId, PolicyInput input, CancellationToken ct)
    {
        var policy = await db.Policies.SingleOrDefaultAsync(p => p.Id == policyId && !p.Deleted, ct) ?? throw AppException.NotFound("Policy not found");
        var title = input.Title.Trim();
        var body = input.Body.Trim();
        // A changed title or text is a new version that staff must confirm again; category or visibility is not.
        if (title != policy.Title || body != policy.Body) policy.Version++;
        policy.Title = title;
        policy.Body = body;
        policy.Category = input.Category.Trim();
        policy.Published = input.Published;
        policy.UpdatedAt = clock.GetUtcNow();
        policy.UpdatedBy = actorId;
        audit.Add("policy_updated", actorId, NoContext, new { policyId, policy.Version, policy.Published });
        await db.SaveChangesAsync(ct);
        var ack = await db.PolicyAcknowledgements.AsNoTracking().SingleOrDefaultAsync(a => a.PolicyId == policyId && a.UserId == actorId, ct);
        return ToDto(policy, await EditorNamesAsync([actorId], ct), ack);
    }

    public async Task DeleteAsync(Guid actorId, Guid policyId, CancellationToken ct)
    {
        var policy = await db.Policies.SingleOrDefaultAsync(p => p.Id == policyId && !p.Deleted, ct) ?? throw AppException.NotFound("Policy not found");
        // Kept in the database so past confirmations stay traceable.
        policy.Deleted = true;
        policy.Published = false;
        audit.Add("policy_deleted", actorId, NoContext, new { policyId, policy.Title });
        await db.SaveChangesAsync(ct);
    }

    public async Task AcknowledgeAsync(Guid userId, Guid policyId, CancellationToken ct)
    {
        var policy = await db.Policies.AsNoTracking().SingleOrDefaultAsync(p => p.Id == policyId && !p.Deleted && p.Published, ct)
            ?? throw AppException.NotFound("Policy not found");
        var ack = await db.PolicyAcknowledgements.SingleOrDefaultAsync(a => a.PolicyId == policyId && a.UserId == userId, ct);
        if (ack is null)
        {
            ack = new PolicyAcknowledgement { PolicyId = policyId, UserId = userId };
            db.PolicyAcknowledgements.Add(ack);
        }
        else if (ack.Version == policy.Version)
        {
            return;
        }
        ack.Version = policy.Version;
        ack.AcknowledgedAt = clock.GetUtcNow();
        audit.Add("policy_acknowledged", userId, NoContext, new { policyId, policy.Version });
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<PolicyAckDto>> AcknowledgementsAsync(Guid policyId, CancellationToken ct)
    {
        if (!await db.Policies.AnyAsync(p => p.Id == policyId && !p.Deleted, ct)) throw AppException.NotFound("Policy not found");
        var acks = await db.PolicyAcknowledgements.AsNoTracking().Where(a => a.PolicyId == policyId).ToDictionaryAsync(a => a.UserId, ct);
        var staff = await users.Users.AsNoTracking().Where(u => u.IsActive).OrderBy(u => u.DisplayName).ToListAsync(ct);
        var result = new List<PolicyAckDto>(staff.Count);
        foreach (var user in staff)
        {
            acks.TryGetValue(user.Id, out var ack);
            result.Add(new PolicyAckDto(user.Id, user.DisplayName, user.Email ?? "", await users.RoleOfAsync(user), ack?.Version, ack?.AcknowledgedAt));
        }
        return result;
    }

    private async Task<Dictionary<Guid, string>> EditorNamesAsync(IEnumerable<Guid> ids, CancellationToken ct)
    {
        var wanted = ids.Distinct().ToList();
        return await users.Users.AsNoTracking().Where(u => wanted.Contains(u.Id)).ToDictionaryAsync(u => u.Id, u => u.DisplayName, ct);
    }

    private static PolicyDto ToDto(Policy p, Dictionary<Guid, string> names, PolicyAcknowledgement? ack) =>
        new(p.Id, p.Title, p.Category, p.Body, p.Published, p.Version, p.UpdatedAt, names.GetValueOrDefault(p.UpdatedBy, "Former staff"), ack?.Version, ack?.AcknowledgedAt);
}
