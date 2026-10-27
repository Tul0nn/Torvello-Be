using Microsoft.EntityFrameworkCore;
using Torvello.Application.Sync;
using Torvello.Infrastructure.Auth;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Sync;

public sealed class SyncService(AppDbContext db, TimeProvider clock) : ISyncService
{
    private const int MaxPayloadChars = 64 * 1024;

    public async Task<SyncPushResult> PushAsync(Guid deviceId, SyncPushRequest request, CancellationToken ct)
    {
        var accepted = new List<Guid>();
        var rejected = new List<RejectedEvent>();

        // Each event is stored on its own, so one bad event never blocks the rest of the queue.
        foreach (var evt in request.Events)
        {
            var raw = evt.Payload.GetRawText();
            var hash = TokenService.Hash(raw);
            try
            {
                if (raw.Length > MaxPayloadChars) throw new SyncRejection("Payload is too large");
                if (await IsDuplicateAsync(evt.EventId, deviceId, hash, ct))
                {
                    accepted.Add(evt.EventId);
                    continue;
                }

                db.SyncEvents.Add(new SyncEvent
                {
                    EventId = evt.EventId,
                    DeviceId = deviceId,
                    EventType = evt.EventType,
                    Payload = raw,
                    PayloadHash = hash,
                    OccurredAt = evt.CreatedAt,
                    ReceivedAt = clock.GetUtcNow(),
                });
                // Other event types (shifts, cash movements, recipes, later attendance) are kept as
                // raw events until their reports exist; they can be projected from history then.
                switch (evt.EventType)
                {
                    case "order_completed":
                        var order = OrderProjection.Parse(evt.Payload, deviceId, evt.EventId, evt.CreatedAt);
                        db.SyncedOrders.Add(order);
                        await InventoryProjection.SaleUsageAsync(db, evt.Payload, order, ct);
                        break;
                    case "ingredient_saved":
                        await InventoryProjection.IngredientSavedAsync(db, evt.Payload, deviceId, evt.CreatedAt, ct);
                        break;
                    case "stock_movement":
                        await InventoryProjection.StockMovementAsync(db, evt.Payload, deviceId, evt.EventId, evt.CreatedAt, ct);
                        break;
                    case "attendance_clock_in":
                        Hr.AttendanceProjection.ClockIn(db, evt.Payload, deviceId);
                        break;
                    case "attendance_clock_out":
                        await Hr.AttendanceProjection.ClockOutAsync(db, evt.Payload, deviceId, ct);
                        break;
                }
                await db.SaveChangesAsync(ct);
                accepted.Add(evt.EventId);
            }
            catch (SyncRejection rejection)
            {
                db.ChangeTracker.Clear();
                rejected.Add(new RejectedEvent(evt.EventId, rejection.Message));
            }
            catch (DbUpdateException)
            {
                db.ChangeTracker.Clear();
                // A parallel upload of the same event may have won the race; that still counts as received.
                try
                {
                    if (await IsDuplicateAsync(evt.EventId, deviceId, hash, ct)) accepted.Add(evt.EventId);
                    else rejected.Add(new RejectedEvent(evt.EventId, "This order was already received from another event"));
                }
                catch (SyncRejection rejection)
                {
                    rejected.Add(new RejectedEvent(evt.EventId, rejection.Message));
                }
            }
        }

        var device = await db.Devices.FindAsync([deviceId], ct);
        if (device is not null)
        {
            device.LastSeenAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct);
        }
        return new SyncPushResult(accepted, rejected);
    }

    public async Task<DevicePingDto> PingAsync(Guid deviceId, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([deviceId], ct) ?? throw new InvalidOperationException("Authenticated device is missing");
        device.LastSeenAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct);
        return new DevicePingDto(device.Id, device.Name);
    }

    /// <summary>True when this exact event was already stored; throws when the id was reused with other content.</summary>
    private async Task<bool> IsDuplicateAsync(Guid eventId, Guid deviceId, string hash, CancellationToken ct)
    {
        var existing = await db.SyncEvents.AsNoTracking().SingleOrDefaultAsync(e => e.EventId == eventId, ct);
        if (existing is null) return false;
        if (existing.DeviceId != deviceId || existing.PayloadHash != hash)
        {
            throw new SyncRejection("Event id was already used with different content");
        }
        return true;
    }
}
