using System.Text.Json;
using Torvello.Application.Auth;

namespace Torvello.Application.Sync;

public sealed record CreateDeviceRequest(string Name);

public sealed record DeviceDto(Guid Id, string Name, DateTimeOffset CreatedAt, DateTimeOffset? LastSeenAt, bool Revoked);

/// <summary>Returned once when a till is registered. The key is shown only here; it is stored hashed.</summary>
public sealed record CreatedDeviceDto(DeviceDto Device, string DeviceKey);

public sealed record SyncEventDto(Guid EventId, string EventType, DateTimeOffset CreatedAt, JsonElement Payload);

public sealed record SyncPushRequest(IReadOnlyList<SyncEventDto> Events);

public sealed record RejectedEvent(Guid EventId, string Reason);

/// <summary>Accepted ids include events already received earlier; the till can mark all of them as sent.</summary>
public sealed record SyncPushResult(IReadOnlyList<Guid> Accepted, IReadOnlyList<RejectedEvent> Rejected);

public sealed record TopItemDto(string Name, int Quantity, long SalesPaisa);

public sealed record DailyReportDto(
    DateOnly Date,
    int OrderCount,
    long SalesPaisa,
    long DiscountPaisa,
    IReadOnlyDictionary<string, long> ByMethod,
    IReadOnlyList<TopItemDto> TopItems,
    DateTimeOffset? LastSyncAt);

public interface IDeviceService
{
    Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct);
    Task<CreatedDeviceDto> CreateAsync(Guid actorId, CreateDeviceRequest request, RequestContext context, CancellationToken ct);
    Task RevokeAsync(Guid actorId, Guid deviceId, RequestContext context, CancellationToken ct);
}

public sealed record DevicePingDto(Guid DeviceId, string Name);

public interface ISyncService
{
    Task<SyncPushResult> PushAsync(Guid deviceId, SyncPushRequest request, CancellationToken ct);
    Task<DevicePingDto> PingAsync(Guid deviceId, CancellationToken ct);
}

public interface IReportService
{
    Task<DailyReportDto> DailyAsync(DateOnly? date, CancellationToken ct);
}
