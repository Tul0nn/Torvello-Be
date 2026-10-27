namespace Torvello.Infrastructure.Persistence;

/// <summary>Ingredient as last saved on a till (ids come from the till).</summary>
public sealed class InventoryIngredient
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string Name { get; set; } = "";
    public string Unit { get; set; } = "";
    public long LowStockMilli { get; set; }
    public bool Active { get; set; } = true;
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? LastMovementAt { get; set; }
}

/// <summary>
/// One stock change, in thousandths of the ingredient's base unit (g, ml, pcs).
/// Manual changes keep the till's id; sale deductions are keyed by (SourceEventId, IngredientId).
/// </summary>
public sealed class InventoryMovement
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid IngredientId { get; set; }
    public Guid DeviceId { get; set; }
    public Guid SourceEventId { get; set; }
    public string Type { get; set; } = "";
    public long QuantityMilli { get; set; }
    public long? CostPaisa { get; set; }
    public string? Note { get; set; }
    public string? EmployeeName { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    /// <summary>OccurredAt as Unix milliseconds, for ordering that works the same in every database.</summary>
    public long OccurredAtMs { get; set; }
}
