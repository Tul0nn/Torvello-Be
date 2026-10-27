namespace Torvello.Infrastructure.Persistence;

/// <summary>A cafe till allowed to upload data. It authenticates with "{Id}.{secret}"; only a hash of the secret is kept.</summary>
public sealed class Device
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    public string KeyHash { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// Every event a till uploads, stored as received. Event ids make uploads idempotent,
/// and keeping the raw payload lets new reports be rebuilt from history later.
/// </summary>
public sealed class SyncEvent
{
    public Guid EventId { get; set; }
    public Guid DeviceId { get; set; }
    public string EventType { get; set; } = "";
    public string Payload { get; set; } = "";
    public string PayloadHash { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}

/// <summary>Completed sale projected from an order_completed event, for owner reports.</summary>
public sealed class SyncedOrder
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public Guid SourceEventId { get; set; }
    public string ReceiptNumber { get; set; } = "";
    public DateOnly BusinessDate { get; set; }
    public string OrderType { get; set; } = "";
    public string? EmployeeName { get; set; }
    public long SubtotalPaisa { get; set; }
    public long DiscountPaisa { get; set; }
    public long TotalPaisa { get; set; }
    public DateTimeOffset CompletedAt { get; set; }

    /// <summary>CompletedAt as Unix milliseconds, for ordering that works the same in every database.</summary>
    public long CompletedAtMs { get; set; }

    public Guid? CustomerId { get; set; }
    public List<SyncedOrderLine> Lines { get; set; } = [];
    public List<SyncedPayment> Payments { get; set; } = [];
}

public sealed class SyncedOrderLine
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Name { get; set; } = "";
    public int Quantity { get; set; }
    public long UnitPricePaisa { get; set; }
    public long LineTotalPaisa { get; set; }
}

public sealed class SyncedPayment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid OrderId { get; set; }
    public string Method { get; set; } = "";
    public long AmountPaisa { get; set; }
}
