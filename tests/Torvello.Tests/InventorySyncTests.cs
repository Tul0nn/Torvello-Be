using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Torvello.Application.Inventory;
using Torvello.Application.Sync;

namespace Torvello.Tests;

public sealed class InventorySyncTests : IDisposable
{
    private readonly TestApp _app = new();
    private static readonly Guid Beans = Guid.NewGuid();
    private static readonly Guid Milk = Guid.NewGuid();

    public void Dispose() => _app.Dispose();

    private static SyncEventDto Event(string type, object payload, DateTimeOffset? at = null) =>
        new(Guid.NewGuid(), type, at ?? DateTimeOffset.Parse("2026-10-27T08:00:00Z"), JsonSerializer.SerializeToElement(payload));

    private static SyncEventDto Ingredient(Guid id, string name, string unit, long lowStock) =>
        Event("ingredient_saved", new { id = id.ToString(), name, unit, lowStockMilli = lowStock, active = true });

    private static SyncEventDto Movement(Guid ingredientId, string type, long quantity, long? cost = null, string at = "2026-10-27T08:05:00Z") =>
        Event("stock_movement", new
        {
            id = Guid.NewGuid().ToString(),
            ingredientId = ingredientId.ToString(),
            type,
            quantityMilli = quantity,
            costPaisa = cost,
            note = "Supplier A",
            createdAt = at,
            employeeName = "Karim",
        });

    private static SyncEventDto Sale(long beansUsed, long milkUsed) =>
        Event("order_completed", new
        {
            orderId = Guid.NewGuid().ToString(),
            receiptNumber = $"TV-20261027-{Random.Shared.Next(1000, 9999)}",
            completedAt = "2026-10-27T09:00:00Z",
            employeeName = "Rahim",
            totals = new { subtotalPaisa = 56000, discountPaisa = 0, totalPaisa = 56000 },
            lines = new object[] { new { name = "Cafe Latte", unitPricePaisa = 28000, quantity = 2 } },
            payments = new object[] { new { method = "cash", amountPaisa = 56000 } },
            stockUsage = new object[]
            {
                new { ingredientId = Beans.ToString(), quantityMilli = beansUsed },
                new { ingredientId = Milk.ToString(), quantityMilli = milkUsed },
            },
        });

    private async Task<(HttpClient Owner, HttpClient Till)> SetupAsync()
    {
        var (owner, _) = await _app.OwnerAsync();
        var device = (await (await owner.PostAsJsonAsync("/api/devices", new CreateDeviceRequest("Counter"))).Content.ReadFromJsonAsync<CreatedDeviceDto>())!;
        var till = _app.CreateClient();
        till.DefaultRequestHeaders.Add("X-Device-Key", device.DeviceKey);
        return (owner, till);
    }

    private static async Task<SyncPushResult> PushAsync(HttpClient till, params SyncEventDto[] events) =>
        (await (await till.PostAsJsonAsync("/api/sync/events", new SyncPushRequest(events))).Content.ReadFromJsonAsync<SyncPushResult>())!;

    [Fact]
    public async Task Deliveries_wastage_and_sales_give_the_stock_level()
    {
        var (owner, till) = await SetupAsync();
        var sale = Sale(beansUsed: 36_000, milkUsed: 400_000);
        var result = await PushAsync(
            till,
            Ingredient(Beans, "Coffee beans", "g", lowStock: 500_000),
            Ingredient(Milk, "Milk", "ml", lowStock: 2_000_000),
            Movement(Beans, "purchase", 1_000_000, cost: 200_000),
            Movement(Milk, "purchase", 2_000_000, cost: 24_000),
            Movement(Milk, "wastage", -250_000),
            sale);
        Assert.Empty(result.Rejected);

        // The same sale uploaded again must not deduct twice.
        Assert.Single((await PushAsync(till, sale)).Accepted);

        var items = (await owner.GetFromJsonAsync<List<InventoryItemDto>>("/api/inventory"))!;
        var beans = items.Single(i => i.Name == "Coffee beans");
        var milk = items.Single(i => i.Name == "Milk");
        Assert.Equal(964_000, beans.StockMilli);
        Assert.False(beans.IsLow);
        Assert.Equal(1_350_000, milk.StockMilli);
        Assert.True(milk.IsLow);
        Assert.Equal("Milk", items[0].Name); // low stock first

        var history = (await owner.GetFromJsonAsync<List<StockMovementDto>>($"/api/inventory/{Milk}/movements"))!;
        Assert.Equal(["sale", "wastage", "purchase"], history.Select(m => m.Type));
        Assert.Equal(-400_000, history[0].QuantityMilli);
        Assert.Equal("Rahim", history[0].EmployeeName);
        Assert.Equal(24_000, history[2].CostPaisa);
    }

    [Fact]
    public async Task Invalid_stock_events_are_rejected_with_a_reason()
    {
        var (_, till) = await SetupAsync();
        var result = await PushAsync(
            till,
            Ingredient(Beans, "Coffee beans", "kg", lowStock: 0),
            Movement(Beans, "wastage", 5_000),
            Movement(Beans, "theft", -5_000));

        Assert.Equal(3, result.Rejected.Count);
        Assert.Contains("unit", result.Rejected[0].Reason);
        Assert.Contains("Wastage must reduce stock", result.Rejected[1].Reason);
        Assert.Contains("Unknown stock movement type", result.Rejected[2].Reason);
    }

    [Fact]
    public async Task Cashiers_cannot_see_inventory()
    {
        var (owner, _) = await SetupAsync();
        await owner.PostAsJsonAsync("/api/staff", new Application.Staff.InviteStaffRequest("cash@torvello.test", "Cash", Domain.Roles.Cashier));
        var client = _app.CreateClient();
        await client.PostAsJsonAsync("/api/auth/accept-invite", new Application.Auth.AcceptInviteRequest("cash@torvello.test", _app.Mail.LatestTokenFor("cash@torvello.test"), "Mocha7788x"));
        var cashier = _app.ClientWith((await _app.LoginAsync(client, "cash@torvello.test", "Mocha7788x")).AccessToken);

        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/inventory")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await owner.GetAsync($"/api/inventory/{Guid.NewGuid()}/movements")).StatusCode);
    }
}
