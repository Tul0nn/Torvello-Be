using System.Security.Cryptography;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Torvello.Application.Auth;
using Torvello.Application.Common;
using Torvello.Application.Sync;
using Torvello.Infrastructure.Auth;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Sync;

public sealed class DeviceService(AppDbContext db, AuditLog audit, TimeProvider clock) : IDeviceService
{
    public async Task<IReadOnlyList<DeviceDto>> ListAsync(CancellationToken ct)
    {
        var devices = await db.Devices.AsNoTracking().ToListAsync(ct);
        return devices.OrderBy(d => d.RevokedAt is not null).ThenBy(d => d.Name).Select(ToDto).ToList();
    }

    public async Task<CreatedDeviceDto> CreateAsync(Guid actorId, CreateDeviceRequest request, RequestContext context, CancellationToken ct)
    {
        var secret = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var device = new Device
        {
            Name = request.Name.Trim(),
            KeyHash = TokenService.Hash(secret),
            CreatedAt = clock.GetUtcNow(),
            CreatedBy = actorId,
        };
        db.Devices.Add(device);
        audit.Add("device_registered", actorId, context, new { deviceId = device.Id, device.Name });
        await db.SaveChangesAsync(ct);
        return new CreatedDeviceDto(ToDto(device), $"{device.Id}.{secret}");
    }

    public async Task RevokeAsync(Guid actorId, Guid deviceId, RequestContext context, CancellationToken ct)
    {
        var device = await db.Devices.FindAsync([deviceId], ct) ?? throw AppException.NotFound("Till not found");
        if (device.RevokedAt is not null) return;
        device.RevokedAt = clock.GetUtcNow();
        audit.Add("device_revoked", actorId, context, new { deviceId });
        await db.SaveChangesAsync(ct);
    }

    /// <summary>Checks a "{deviceId}.{secret}" key in constant time. Returns the device id or null.</summary>
    public static async Task<Guid?> AuthenticateAsync(AppDbContext db, string? key, CancellationToken ct)
    {
        var parts = key?.Split('.', 2);
        if (parts is not { Length: 2 } || !Guid.TryParse(parts[0], out var id) || parts[1].Length is 0 or > 100) return null;
        var device = await db.Devices.AsNoTracking().SingleOrDefaultAsync(d => d.Id == id, ct);
        if (device is null || device.RevokedAt is not null) return null;
        var expected = Convert.FromHexString(device.KeyHash);
        var actual = Convert.FromHexString(TokenService.Hash(parts[1]));
        return CryptographicOperations.FixedTimeEquals(expected, actual) ? device.Id : null;
    }

    private static DeviceDto ToDto(Device d) => new(d.Id, d.Name, d.CreatedAt, d.LastSeenAt, d.RevokedAt is not null);
}
