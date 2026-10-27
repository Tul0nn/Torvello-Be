using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Torvello.Application.Sync;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Sync;

/// <summary>
/// Website edits that tills download. Payloads are encrypted at rest because staff changes carry national IDs.
/// Callers save the change in the same SaveChanges as the edit itself.
/// </summary>
public sealed class ChangeFeed(AppDbContext db, IDataProtectionProvider protection, TimeProvider clock)
{
    private const int PageSize = 200;
    private readonly IDataProtector _protector = protection.CreateProtector("Torvello.ServerChanges.v1");

    public void Add(string entityType, Guid entityId, object payload) =>
        db.ServerChanges.Add(new ServerChange
        {
            EntityType = entityType,
            EntityId = entityId,
            ProtectedPayload = _protector.Protect(JsonSerializer.Serialize(payload, JsonSerializerOptions.Web)),
            CreatedAt = clock.GetUtcNow(),
        });

    public async Task<ServerChangesDto> AfterAsync(long after, CancellationToken ct)
    {
        var rows = await db.ServerChanges.AsNoTracking()
            .Where(c => c.Sequence > after)
            .OrderBy(c => c.Sequence)
            .Take(PageSize + 1)
            .ToListAsync(ct);
        var page = rows.Take(PageSize).ToList();
        var items = page
            .Select(c => new ServerChangeDto(c.Sequence, c.EntityType, JsonSerializer.Deserialize<JsonElement>(_protector.Unprotect(c.ProtectedPayload))))
            .ToList();
        return new ServerChangesDto(items, page.Count > 0 ? page[^1].Sequence : after, rows.Count > PageSize);
    }
}
