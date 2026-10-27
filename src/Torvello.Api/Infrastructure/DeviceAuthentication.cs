using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using Torvello.Infrastructure.Persistence;
using Torvello.Infrastructure.Sync;

namespace Torvello.Api.Infrastructure;

/// <summary>Authenticates cafe tills by the "X-Device-Key: {deviceId}.{secret}" header.</summary>
public sealed class DeviceAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder,
    AppDbContext db) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "Device";
    public const string Header = "X-Device-Key";
    public const string DeviceIdClaim = "device_id";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(Header, out var key)) return AuthenticateResult.NoResult();
        var deviceId = await DeviceService.AuthenticateAsync(db, key.ToString(), Context.RequestAborted);
        if (deviceId is null) return AuthenticateResult.Fail("Unknown or revoked till key");

        var identity = new ClaimsIdentity([new Claim(DeviceIdClaim, deviceId.Value.ToString())], SchemeName);
        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}

public static class DevicePrincipal
{
    public static Guid DeviceId(this ClaimsPrincipal user) => Guid.Parse(user.FindFirstValue(DeviceAuthenticationHandler.DeviceIdClaim)!);
}
