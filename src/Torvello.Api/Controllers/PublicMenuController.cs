using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Cors;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Torvello.Api.Infrastructure;
using Torvello.Application.Sync;

namespace Torvello.Api.Controllers;

[ApiController]
[Route("api/public/menu")]
[AllowAnonymous]
[AllowWhilePasswordChangeRequired]
[EnableCors("PublicMenu")]
[EnableRateLimiting(RateLimits.PublicMenu)]
public sealed class PublicMenuController(IPublicMenuService menus) : ControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Get(CancellationToken ct)
    {
        var menu = await menus.GetAsync(ct);
        if (menu is null) return NotFound(new ProblemDetails { Status = 404, Title = "Menu not published yet" });
        var json = JsonSerializer.SerializeToUtf8Bytes(menu, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var etag = $"\"{Convert.ToHexString(SHA256.HashData(json))}\"";
        Response.Headers.CacheControl = "public, max-age=60";
        Response.Headers.ETag = etag;
        if (Request.GetTypedHeaders().IfNoneMatch?.Any(tag => tag.Tag.Value == "*" || tag.Tag.Value == etag) == true)
            return StatusCode(StatusCodes.Status304NotModified);
        return File(json, "application/json");
    }
}
