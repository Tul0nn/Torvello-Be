using Microsoft.EntityFrameworkCore;
using Torvello.Application.Common;
using Torvello.Application.Inventory;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Sync;

public sealed class InventoryService(AppDbContext db) : IInventoryService
{
    private const int MovementLimit = 100;

    public async Task<IReadOnlyList<InventoryItemDto>> ListAsync(CancellationToken ct)
    {
        var ingredients = await db.InventoryIngredients.AsNoTracking().ToListAsync(ct);
        var stock = await db.InventoryMovements.AsNoTracking()
            .GroupBy(m => m.IngredientId)
            .Select(g => new { IngredientId = g.Key, Stock = g.Sum(m => m.QuantityMilli) })
            .ToDictionaryAsync(x => x.IngredientId, x => x.Stock, ct);

        return ingredients
            .Select(i =>
            {
                var level = stock.GetValueOrDefault(i.Id);
                return new InventoryItemDto(i.Id, i.Name, i.Unit, level, i.LowStockMilli, i.Active && level <= i.LowStockMilli, i.Active, i.LastMovementAt);
            })
            .OrderByDescending(i => i.IsLow)
            .ThenBy(i => i.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public async Task<IReadOnlyList<StockMovementDto>> MovementsAsync(Guid ingredientId, CancellationToken ct)
    {
        if (!await db.InventoryIngredients.AnyAsync(i => i.Id == ingredientId, ct)) throw AppException.NotFound("Ingredient not found");
        return await db.InventoryMovements.AsNoTracking()
            .Where(m => m.IngredientId == ingredientId)
            .OrderByDescending(m => m.OccurredAtMs)
            .Take(MovementLimit)
            .Select(m => new StockMovementDto(m.Id, m.Type, m.QuantityMilli, m.CostPaisa, m.Note, m.EmployeeName, m.OccurredAt))
            .ToListAsync(ct);
    }
}
