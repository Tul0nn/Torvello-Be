namespace Torvello.Application.Inventory;

/// <summary>Quantities are thousandths of the base unit: g, ml or pcs.</summary>
public sealed record InventoryItemDto(
    Guid Id,
    string Name,
    string Unit,
    long StockMilli,
    long LowStockMilli,
    bool IsLow,
    bool Active,
    DateTimeOffset? LastMovementAt);

public sealed record StockMovementDto(
    Guid Id,
    string Type,
    long QuantityMilli,
    long? CostPaisa,
    string? Note,
    string? EmployeeName,
    DateTimeOffset OccurredAt);

public interface IInventoryService
{
    Task<IReadOnlyList<InventoryItemDto>> ListAsync(CancellationToken ct);
    Task<IReadOnlyList<StockMovementDto>> MovementsAsync(Guid ingredientId, CancellationToken ct);
}
