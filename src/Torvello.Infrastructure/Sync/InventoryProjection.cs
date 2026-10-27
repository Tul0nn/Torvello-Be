using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Sync;

/// <summary>Applies inventory events from a till. Callers save the changes with the event itself.</summary>
public static class InventoryProjection
{
    private static readonly HashSet<string> Units = ["g", "ml", "pcs"];
    private static readonly HashSet<string> ManualTypes = ["opening", "purchase", "wastage", "count"];
    private const long MaxMilli = 1_000_000_000_000;

    public static async Task IngredientSavedAsync(AppDbContext db, JsonElement payload, Guid deviceId, DateTimeOffset at, CancellationToken ct)
    {
        var id = RequireGuid(payload, "id");
        var name = Str(payload, "name")?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 60) throw new SyncRejection("Ingredient name is missing");
        var unit = Str(payload, "unit");
        if (unit is null || !Units.Contains(unit)) throw new SyncRejection("Unknown ingredient unit");
        var lowStock = Long(payload, "lowStockMilli");
        if (lowStock is < 0 or > MaxMilli) throw new SyncRejection("Invalid low-stock level");
        var active = !payload.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False;

        var ingredient = await db.InventoryIngredients.FindAsync([id], ct);
        if (ingredient is null)
        {
            ingredient = new InventoryIngredient { Id = id, DeviceId = deviceId };
            db.InventoryIngredients.Add(ingredient);
        }
        else if (ingredient.DeviceId != deviceId)
        {
            throw new SyncRejection("This ingredient belongs to another till");
        }
        ingredient.Name = name;
        ingredient.Unit = unit;
        ingredient.LowStockMilli = lowStock;
        ingredient.Active = active;
        ingredient.UpdatedAt = at;
    }

    public static async Task StockMovementAsync(AppDbContext db, JsonElement payload, Guid deviceId, Guid eventId, DateTimeOffset fallbackAt, CancellationToken ct)
    {
        var type = Str(payload, "type");
        if (type is null || !ManualTypes.Contains(type)) throw new SyncRejection("Unknown stock movement type");
        var quantity = Long(payload, "quantityMilli");
        if (quantity == 0 || Math.Abs(quantity) > MaxMilli) throw new SyncRejection("Invalid stock quantity");
        if ((type is "purchase" or "opening") && quantity < 0) throw new SyncRejection("Received stock must be positive");
        if (type == "wastage" && quantity > 0) throw new SyncRejection("Wastage must reduce stock");

        long? cost = null;
        if (payload.TryGetProperty("costPaisa", out var c) && c.ValueKind == JsonValueKind.Number)
        {
            cost = c.TryGetInt64(out var paisa) && paisa >= 0 ? paisa : throw new SyncRejection("Invalid cost");
        }
        var at = Date(payload, "createdAt") ?? fallbackAt;
        await AddAsync(db, new InventoryMovement
        {
            Id = RequireGuid(payload, "id"),
            IngredientId = RequireGuid(payload, "ingredientId"),
            DeviceId = deviceId,
            SourceEventId = eventId,
            Type = type,
            QuantityMilli = quantity,
            CostPaisa = cost,
            Note = Clip(Str(payload, "note"), 200),
            EmployeeName = Clip(Str(payload, "employeeName"), 60),
            OccurredAt = at,
            OccurredAtMs = at.ToUnixTimeMilliseconds(),
        }, ct);
    }

    /// <summary>Ingredients used by a sale (from the order's recipe usage), recorded as negative movements.</summary>
    public static async Task SaleUsageAsync(AppDbContext db, JsonElement payload, SyncedOrder order, CancellationToken ct)
    {
        if (!payload.TryGetProperty("stockUsage", out var usage) || usage.ValueKind != JsonValueKind.Array) return;
        var seen = new HashSet<Guid>();
        foreach (var item in usage.EnumerateArray())
        {
            var ingredientId = RequireGuid(item, "ingredientId");
            if (!seen.Add(ingredientId)) throw new SyncRejection("An ingredient is listed twice in stock usage");
            var quantity = Long(item, "quantityMilli");
            if (quantity is <= 0 or > MaxMilli) throw new SyncRejection("Invalid stock usage");
            await AddAsync(db, new InventoryMovement
            {
                IngredientId = ingredientId,
                DeviceId = order.DeviceId,
                SourceEventId = order.SourceEventId,
                Type = "sale",
                QuantityMilli = -quantity,
                EmployeeName = order.EmployeeName,
                OccurredAt = order.CompletedAt,
                OccurredAtMs = order.CompletedAt.ToUnixTimeMilliseconds(),
            }, ct);
        }
    }

    private static async Task AddAsync(AppDbContext db, InventoryMovement movement, CancellationToken ct)
    {
        db.InventoryMovements.Add(movement);
        // Movements may arrive before their ingredient on a brand-new till; the ingredient catches up later.
        var ingredient = db.InventoryIngredients.Local.FirstOrDefault(i => i.Id == movement.IngredientId)
            ?? await db.InventoryIngredients.FirstOrDefaultAsync(i => i.Id == movement.IngredientId, ct);
        if (ingredient is not null && (ingredient.LastMovementAt is null || ingredient.LastMovementAt < movement.OccurredAt))
        {
            ingredient.LastMovementAt = movement.OccurredAt;
        }
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string? Clip(string? text, int max) => string.IsNullOrWhiteSpace(text) ? null : text.Trim()[..Math.Min(text.Trim().Length, max)];

    private static long Long(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) ? n : throw new SyncRejection($"{name} is missing");

    private static Guid RequireGuid(JsonElement e, string name) =>
        Guid.TryParse(Str(e, name), out var id) ? id : throw new SyncRejection($"{name} is missing");

    private static DateTimeOffset? Date(JsonElement e, string name) =>
        DateTimeOffset.TryParse(Str(e, name), out var at) ? at : null;
}
