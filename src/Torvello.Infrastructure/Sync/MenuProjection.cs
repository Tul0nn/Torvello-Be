using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Torvello.Application.Sync;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Sync;

public static class MenuProjection
{
    public static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public static MenuSnapshot Parse(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Object ||
            !payload.TryGetProperty("publishedAt", out var at) || at.ValueKind != JsonValueKind.String ||
            !at.TryGetDateTimeOffset(out var publishedAt)) throw new SyncRejection("publishedAt is invalid");
        var count = 0;
        var categories = Array(payload, "categories").Select(category =>
        {
            var items = Array(category, "items").Select(item =>
            {
                if (++count > 500) throw new SyncRejection("A menu allows at most 500 items");
                var sizes = Options(item, "sizes");
                if (sizes.Count == 0) throw new SyncRejection("An item needs at least one size");
                string? description = null;
                if (item.TryGetProperty("description", out var d) && d.ValueKind != JsonValueKind.Null)
                {
                    if (d.ValueKind != JsonValueKind.String) throw new SyncRejection("description is invalid");
                    description = d.GetString();
                }
                return new MenuItemDto(Text(item, "id"), Text(item, "name", 80), description, sizes, Options(item, "addOns"));
            }).ToList();
            if (items.Count == 0) throw new SyncRejection("Empty categories must be omitted");
            if (!category.TryGetProperty("sortOrder", out var sort) || sort.ValueKind != JsonValueKind.Number || !sort.TryGetInt32(out var sortOrder))
                throw new SyncRejection("sortOrder is invalid");
            return new MenuCategoryDto(Text(category, "id"), Text(category, "name", 80), sortOrder, items);
        }).ToList();
        if (categories.Count > 50) throw new SyncRejection("A menu allows at most 50 categories");
        return new MenuSnapshot(DateTimeOffset.FromUnixTimeMilliseconds(publishedAt.ToUnixTimeMilliseconds()), categories);
    }

    private static List<MenuOptionDto> Options(JsonElement item, string name) => Array(item, name).Select(option =>
    {
        if (option.ValueKind != JsonValueKind.Object || !option.TryGetProperty("pricePaisa", out var price) || price.ValueKind != JsonValueKind.Number ||
            !price.TryGetInt64(out var paisa) || paisa is < 0 or > 10_000_000) throw new SyncRejection("pricePaisa is invalid");
        return new MenuOptionDto(Text(option, "id"), Text(option, "name", 80), paisa);
    }).ToList();

    private static IEnumerable<JsonElement> Array(JsonElement value, string name) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var array) && array.ValueKind == JsonValueKind.Array
            ? array.EnumerateArray() : throw new SyncRejection($"{name} must be an array");

    private static string Text(JsonElement value, string name, int max = 200) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(name, out var text) && text.ValueKind == JsonValueKind.String &&
        text.GetString() is { } s && !string.IsNullOrWhiteSpace(s) && s.Length <= max
            ? s : throw new SyncRejection($"{name} is invalid");

    public static async Task ApplyAsync(AppDbContext db, JsonElement payload, DateTimeOffset receivedAt, CancellationToken ct)
    {
        var snapshot = Parse(payload);
        var json = JsonSerializer.Serialize(snapshot, JsonOptions);
        var ms = snapshot.PublishedAt.ToUnixTimeMilliseconds();
        // Atomic compare-and-replace works in PostgreSQL and SQLite, including concurrent first uploads.
        await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO "PublishedMenus" ("CafeId", "Payload", "PublishedAtMs", "ReceivedAt")
            VALUES ({PublishedMenu.DefaultCafeId}, {json}, {ms}, {receivedAt})
            ON CONFLICT ("CafeId") DO UPDATE SET "Payload" = excluded."Payload",
                "PublishedAtMs" = excluded."PublishedAtMs", "ReceivedAt" = excluded."ReceivedAt"
            WHERE excluded."PublishedAtMs" > "PublishedMenus"."PublishedAtMs"
            """, ct);
    }
}

public sealed class PublicMenuService(AppDbContext db, IConfiguration config) : IPublicMenuService
{
    public async Task<PublicMenuDto?> GetAsync(CancellationToken ct)
    {
        var menu = await db.PublishedMenus.AsNoTracking().SingleOrDefaultAsync(m => m.CafeId == PublishedMenu.DefaultCafeId, ct);
        if (menu is null) return null;
        // Parse through the same whitelist even if stored data acquired extra fields.
        using var document = JsonDocument.Parse(menu.Payload);
        var snapshot = MenuProjection.Parse(document.RootElement);
        return new PublicMenuDto(config["PublicSite:CafeName"] ?? "Torvello", snapshot.PublishedAt, snapshot.Categories);
    }
}
