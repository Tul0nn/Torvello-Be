using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Torvello.Api.Infrastructure;
using Torvello.Application.Inventory;
using Torvello.Application.Sync;
using Torvello.Domain;

namespace Torvello.Api.Controllers;

/// <summary>Owner registers cafe tills and gets the key to paste into the till's Cloud screen.</summary>
[ApiController]
[Route("api/devices")]
[Authorize(Roles = Roles.Owner)]
public sealed class DevicesController(IDeviceService devices) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<DeviceDto>> List(CancellationToken ct) => devices.ListAsync(ct);

    [HttpPost]
    public async Task<ActionResult<CreatedDeviceDto>> Create(CreateDeviceRequest request, CancellationToken ct)
    {
        var created = await devices.CreateAsync(User.UserId(), request, HttpContext.ToRequestContext(), ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Revoke(Guid id, CancellationToken ct)
    {
        await devices.RevokeAsync(User.UserId(), id, HttpContext.ToRequestContext(), ct);
        return NoContent();
    }
}

/// <summary>Tills upload their outbox here. Safe to retry: events already received are accepted again.</summary>
[ApiController]
[Route("api/sync")]
[Authorize(AuthenticationSchemes = DeviceAuthenticationHandler.SchemeName)]
public sealed class SyncController(ISyncService sync) : ControllerBase
{
    [HttpPost("events")]
    [RequestSizeLimit(4 * 1024 * 1024)]
    public Task<SyncPushResult> Push(SyncPushRequest request, CancellationToken ct) => sync.PushAsync(User.DeviceId(), request, ct);

    /// <summary>Website edits for the till to apply, after the till's last cursor.</summary>
    [HttpGet("changes")]
    public Task<ServerChangesDto> Changes([FromQuery] long after, CancellationToken ct) => sync.ChangesAsync(Math.Max(0, after), ct);

    /// <summary>Lets a till check its key and show which registered till it is.</summary>
    [HttpGet("ping")]
    public Task<DevicePingDto> Ping(CancellationToken ct) => sync.PingAsync(User.DeviceId(), ct);
}

/// <summary>Stock as recorded on the tills. Read-only: stock changes happen at the till.</summary>
[ApiController]
[Route("api/inventory")]
[Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
public sealed class InventoryController(IInventoryService inventory) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<InventoryItemDto>> List(CancellationToken ct) => inventory.ListAsync(ct);

    [HttpGet("{id:guid}/movements")]
    public Task<IReadOnlyList<StockMovementDto>> Movements(Guid id, CancellationToken ct) => inventory.MovementsAsync(id, ct);
}

[ApiController]
[Route("api/reports")]
[Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
public sealed class ReportsController(IReportService reports) : ControllerBase
{
    /// <summary>Sales for one business day (Bangladesh time); defaults to today.</summary>
    [HttpGet("daily")]
    public Task<DailyReportDto> Daily([FromQuery] DateOnly? date, CancellationToken ct) => reports.DailyAsync(date, ct);
}
