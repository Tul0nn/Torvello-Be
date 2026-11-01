using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Torvello.Application.Sync;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Sync;

/// <summary>
/// Website edits that tills download. Payloads are encrypted at rest because staff changes carry national IDs.
/// Callers save the change in the same SaveChanges as the edit itself.
/// </summary>
public sealed class ChangeFeed(AppDbContext db, IDataProtectionProvider protection, TimeProvider clock, ILogger<ChangeFeed> logger)
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
        var items = new List<ServerChangeDto>(page.Count);
        foreach (var change in page)
        {
            // A change encrypted with a key this server no longer has (lost or replaced key ring) can never be read.
            // Skip it so the tills keep syncing, and log it so the owner can redo that edit.
            if (TryReveal(change.ProtectedPayload) is { } json)
                items.Add(new ServerChangeDto(change.Sequence, change.EntityType, JsonSerializer.Deserialize<JsonElement>(json)));
            else
                logger.LogError("Skipped server change {Sequence} ({EntityType} {EntityId}): it cannot be decrypted with the current keys",
                    change.Sequence, change.EntityType, change.EntityId);
        }
        return new ServerChangesDto(items, page.Count > 0 ? page[^1].Sequence : after, rows.Count > PageSize);
    }

    private string? TryReveal(string payload)
    {
        try
        {
            return _protector.Unprotect(payload);
        }
        catch (CryptographicException)
        {
            return null;
        }
    }
}
