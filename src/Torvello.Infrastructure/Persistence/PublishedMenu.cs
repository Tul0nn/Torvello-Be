namespace Torvello.Infrastructure.Persistence;

public sealed class PublishedMenu
{
    // All currently registered devices belong to the same cafe.
    public const string DefaultCafeId = "default";
    public string CafeId { get; set; } = DefaultCafeId;
    public string Payload { get; set; } = "";
    public long PublishedAtMs { get; set; }
    public DateTimeOffset ReceivedAt { get; set; }
}
