using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Torvello.Application.Sync;
using Torvello.Infrastructure.Persistence;
using Torvello.Infrastructure.Sync;

namespace Torvello.Tests;

public sealed class PublicMenuTests : IDisposable
{
    private readonly TestApp _app = new();
    public void Dispose() => _app.Dispose();

    private static JsonObject Snapshot(string at = "2026-11-08T10:00:00.000Z") => JsonNode.Parse($$"""
        {"publishedAt":"{{at}}","recipes":["secret"],"categories":[
          {"id":"coffee","name":"Coffee","sortOrder":1,"items":[
            {"id":"mocha","name":"Mocha","description":null,"costPaisa":12,"stock":9,
             "ingredients":["secret"],"staff":"private","sales":45,
             "sizes":[{"id":"regular","name":"Regular","pricePaisa":28000,"recipe":"secret"}],
             "addOns":[{"id":"syrup","name":"Extra syrup","pricePaisa":3000,"cost":1}]}]}]}
        """)!.AsObject();

    private static SyncEventDto Event(JsonObject payload) => new(Guid.NewGuid(), "menu_published", DateTimeOffset.UtcNow,
        JsonSerializer.SerializeToElement(payload));

    private async Task<HttpClient> TillAsync()
    {
        var (owner, _) = await _app.OwnerAsync();
        var response = await owner.PostAsJsonAsync("/api/devices", new CreateDeviceRequest("Counter"));
        response.EnsureSuccessStatusCode();
        var device = (await response.Content.ReadFromJsonAsync<CreatedDeviceDto>())!;
        var till = _app.CreateClient();
        till.DefaultRequestHeaders.Add("X-Device-Key", device.DeviceKey);
        return till;
    }

    private static async Task PushAsync(HttpClient till, params SyncEventDto[] events)
    {
        var response = await till.PostAsJsonAsync("/api/sync/events", new SyncPushRequest(events));
        response.EnsureSuccessStatusCode();
        var result = (await response.Content.ReadFromJsonAsync<SyncPushResult>())!;
        Assert.Empty(result.Rejected);
        Assert.Equal(events.Select(e => e.EventId), result.Accepted);
    }

    [Fact]
    public async Task Snapshot_is_stored_idempotently_and_newest_wins()
    {
        var till = await TillAsync();
        var newest = Event(Snapshot());
        await PushAsync(till, newest, newest);
        var older = Event(Snapshot("2026-11-08T09:00:00Z"));
        var equalPayload = Snapshot();
        equalPayload["categories"]![0]!["name"] = "Must not replace";
        await PushAsync(till, older, Event(equalPayload));
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.Equal(3, await db.SyncEvents.CountAsync(e => e.EventType == "menu_published"));
        var stored = Assert.Single(await db.PublishedMenus.ToListAsync());
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T10:00:00Z").ToUnixTimeMilliseconds(), stored.PublishedAtMs);
        Assert.DoesNotContain("Must not replace", stored.Payload);
        Assert.DoesNotContain("secret", stored.Payload);
        Assert.NotEqual(default, stored.ReceivedAt);
        var response = await _app.CreateClient().GetAsync("/api/public/menu");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var dto = (await response.Content.ReadFromJsonAsync<PublicMenuDto>())!;
        Assert.Equal("Torvello", dto.CafeName);
        Assert.Equal("Coffee", Assert.Single(dto.Categories).Name);
        Assert.Equal(28000, Assert.Single(Assert.Single(dto.Categories).Items).Sizes[0].PricePaisa);
    }

    [Fact]
    public async Task Public_response_whitelists_fields_and_supports_conditional_get()
    {
        await PushAsync(await TillAsync(), Event(Snapshot()));
        // Even additional fields in persisted JSON cannot escape the response DTO.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            (await db.PublishedMenus.SingleAsync()).Payload = Snapshot().ToJsonString();
            await db.SaveChangesAsync();
        }
        var client = _app.CreateClient();
        var response = await client.GetAsync("/api/public/menu");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(new[] { "cafeName", "updatedAt", "categories" }, json.RootElement.EnumerateObject().Select(p => p.Name));
        var category = json.RootElement.GetProperty("categories")[0];
        Assert.Equal(new[] { "id", "name", "sortOrder", "items" }, category.EnumerateObject().Select(p => p.Name));
        var item = category.GetProperty("items")[0];
        Assert.Equal(new[] { "id", "name", "description", "sizes", "addOns" }, item.EnumerateObject().Select(p => p.Name));
        foreach (var field in new[] { "sizes", "addOns" })
            Assert.Equal(new[] { "id", "name", "pricePaisa" }, item.GetProperty(field)[0].EnumerateObject().Select(p => p.Name));
        Assert.Equal("public, max-age=60", response.Headers.CacheControl!.ToString());
        var etag = response.Headers.ETag!.ToString();
        foreach (var match in new[] { etag, "W/" + etag, "\"unrelated\", " + etag, "*" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "/api/public/menu");
            request.Headers.TryAddWithoutValidation("If-None-Match", match);
            var cached = await client.SendAsync(request);
            Assert.Equal(HttpStatusCode.NotModified, cached.StatusCode);
            Assert.Equal(etag, cached.Headers.ETag!.ToString());
            Assert.Equal("public, max-age=60", cached.Headers.CacheControl!.ToString());
            Assert.Empty(await cached.Content.ReadAsByteArrayAsync());
        }
        var newer = Snapshot("2026-11-08T11:00:00Z");
        // Use the existing authenticated device through the service for the new snapshot.
        using (var scope = _app.Services.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var sync = scope.ServiceProvider.GetRequiredService<ISyncService>();
            await sync.PushAsync((await db.Devices.SingleAsync()).Id, new SyncPushRequest([Event(newer)]), default);
        }
        Assert.NotEqual(etag, (await client.GetAsync("/api/public/menu")).Headers.ETag!.ToString());
    }

    [Theory]
    [InlineData("https://cafe.test", true)]
    [InlineData("https://owner.test", false)]
    [InlineData("https://evil.test", false)]
    public async Task Cors_only_allows_public_site_origins(string origin, bool allowed)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/public/menu");
        request.Headers.Add("Origin", origin);
        var response = await _app.CreateClient().SendAsync(request);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
        if (allowed)
        {
            Assert.Equal(origin, Assert.Single(response.Headers.GetValues("Access-Control-Allow-Origin")));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("Menu not published yet", json.RootElement.GetProperty("title").GetString());
    }

    [Fact]
    public async Task All_devices_publish_to_the_same_cafe()
    {
        var till = await TillAsync();
        await PushAsync(till, Event(Snapshot()));
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var first = await db.Devices.SingleAsync();
        var second = new Device { Name = "Second counter", KeyHash = first.KeyHash, CreatedAt = DateTimeOffset.UtcNow, CreatedBy = first.CreatedBy };
        db.Devices.Add(second);
        await db.SaveChangesAsync();
        var sync = scope.ServiceProvider.GetRequiredService<ISyncService>();
        var newer = Event(Snapshot("2026-11-08T12:00:00Z"));
        var result = await sync.PushAsync(second.Id, new SyncPushRequest([newer]), default);
        Assert.Empty(result.Rejected);
        var stored = Assert.Single(await db.PublishedMenus.AsNoTracking().ToListAsync());
        Assert.Equal(DateTimeOffset.Parse("2026-11-08T12:00:00Z").ToUnixTimeMilliseconds(), stored.PublishedAtMs);
    }

    [Fact]
    public async Task Public_endpoint_has_its_own_sixty_per_minute_limit()
    {
        var client = _app.CreateClient();
        for (var i = 0; i < 60; i++)
            Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/api/public/menu")).StatusCode);
        Assert.Equal(HttpStatusCode.TooManyRequests, (await client.GetAsync("/api/public/menu")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.GetAsync("/api/auth/me")).StatusCode);
    }

    [Fact]
    public void Validation_accepts_boundary_prices_names_and_menu_sizes()
    {
        var payload = Snapshot();
        var item = payload["categories"]![0]!["items"]![0]!;
        item["name"] = new string('a', 80);
        item["sizes"]![0]!["pricePaisa"] = 0;
        item["addOns"]![0]!["pricePaisa"] = 10_000_000;
        payload["categories"]![0]!["items"] = new JsonArray(Enumerable.Range(0, 10).Select(_ => item.DeepClone()).ToArray());
        var category = payload["categories"]![0]!;
        payload["categories"] = new JsonArray(Enumerable.Range(0, 50).Select(_ => category.DeepClone()).ToArray());
        var parsed = MenuProjection.Parse(JsonSerializer.SerializeToElement(payload));
        Assert.Equal(50, parsed.Categories.Count);
        Assert.Equal(500, parsed.Categories.Sum(c => c.Items.Count));
        payload["categories"] = new JsonArray();
        Assert.Empty(MenuProjection.Parse(JsonSerializer.SerializeToElement(payload)).Categories);
    }
    [Theory]
    [InlineData("sizes")]
    [InlineData("negative")]
    [InlineData("tooExpensive")]
    [InlineData("fraction")]
    [InlineData("name")]
    [InlineData("categories")]
    [InlineData("items")]
    [InlineData("timestamp")]
    [InlineData("shape")]
    public void Invalid_snapshots_are_rejected(string scenario)
    {
        var payload = Snapshot();
        var category = payload["categories"]![0]!;
        var item = category["items"]![0]!;
        switch (scenario)
        {
            case "sizes": item["sizes"] = new JsonArray(); break;
            case "negative": item["sizes"]![0]!["pricePaisa"] = -1; break;
            case "tooExpensive": item["addOns"]![0]!["pricePaisa"] = 10_000_001; break;
            case "fraction": item["sizes"]![0]!["pricePaisa"] = 1.5; break;
            case "name": category["name"] = new string('a', 81); break;
            case "categories": payload["categories"] = new JsonArray(Enumerable.Range(0, 51).Select(_ => category.DeepClone()).ToArray()); break;
            case "items": category["items"] = new JsonArray(Enumerable.Range(0, 501).Select(_ => item.DeepClone()).ToArray()); break;
            case "timestamp": payload["publishedAt"] = "invalid"; break;
            case "shape": item["sizes"]![0] = 5; break;
        }
        Assert.Throws<SyncRejection>(() => MenuProjection.Parse(JsonSerializer.SerializeToElement(payload)));
    }

    [Fact]
    public async Task Invalid_event_is_not_stored_and_does_not_block_valid_snapshot()
    {
        var bad = Snapshot();
        bad["categories"]![0]!["items"]![0]!["sizes"] = new JsonArray();
        var invalid = Event(bad);
        var valid = Event(Snapshot());
        var response = await (await TillAsync()).PostAsJsonAsync("/api/sync/events", new SyncPushRequest([invalid, valid]));
        var result = (await response.Content.ReadFromJsonAsync<SyncPushResult>())!;
        Assert.Equal(invalid.EventId, Assert.Single(result.Rejected).EventId);
        Assert.Equal(valid.EventId, Assert.Single(result.Accepted));
        using var scope = _app.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        Assert.False(await db.SyncEvents.AnyAsync(e => e.EventId == invalid.EventId));
        Assert.Single(await db.PublishedMenus.ToListAsync());
    }
}
