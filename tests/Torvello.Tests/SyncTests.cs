using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Torvello.Application.Sync;

namespace Torvello.Tests;

public sealed class SyncTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    private static SyncEventDto OrderEvent(Guid? eventId = null, Guid? orderId = null, long cash = 46000, string receipt = "TV-20261027-0001")
    {
        var payload = new
        {
            orderId = (orderId ?? Guid.NewGuid()).ToString(),
            receiptNumber = receipt,
            businessDate = "2026-10-27",
            completedAt = "2026-10-27T08:15:00.000Z",
            orderType = "takeaway",
            employeeName = "Rahim",
            totals = new { subtotalPaisa = 48000, discountPaisa = 2000, totalPaisa = 46000 },
            lines = new object[]
            {
                new { name = "Cappuccino (Large)", unitPricePaisa = 34000, quantity = 1 },
                new { name = "Croissant", unitPricePaisa = 7000, quantity = 2 },
            },
            payments = new object[] { new { method = "cash", amountPaisa = cash } },
        };
        return new SyncEventDto(eventId ?? Guid.NewGuid(), "order_completed", DateTimeOffset.Parse("2026-10-27T08:15:00Z"), JsonSerializer.SerializeToElement(payload));
    }

    private async Task<(HttpClient Owner, HttpClient Till, CreatedDeviceDto Device)> RegisterTillAsync()
    {
        var (owner, _) = await _app.OwnerAsync();
        var response = await owner.PostAsJsonAsync("/api/devices", new CreateDeviceRequest("Front counter"));
        Assert.Equal(HttpStatusCode.Created, response.StatusCode);
        var device = (await response.Content.ReadFromJsonAsync<CreatedDeviceDto>())!;
        var till = _app.CreateClient();
        till.DefaultRequestHeaders.Add("X-Device-Key", device.DeviceKey);
        return (owner, till, device);
    }

    [Fact]
    public async Task Till_uploads_are_idempotent_and_show_in_the_daily_report()
    {
        var (owner, till, _) = await RegisterTillAsync();
        var ping = (await till.GetFromJsonAsync<DevicePingDto>("/api/sync/ping"))!;
        Assert.Equal("Front counter", ping.Name);
        var first = OrderEvent();
        var shift = new SyncEventDto(Guid.NewGuid(), "shift_opened", DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(new { openingCashPaisa = 200000 }));

        var push = await till.PostAsJsonAsync("/api/sync/events", new SyncPushRequest([first, shift]));
        Assert.Equal(HttpStatusCode.OK, push.StatusCode);
        var result = (await push.Content.ReadFromJsonAsync<SyncPushResult>())!;
        Assert.Equal([first.EventId, shift.EventId], result.Accepted);
        Assert.Empty(result.Rejected);

        // The till retries after a lost response: accepted again, stored once.
        var retry = (await (await till.PostAsJsonAsync("/api/sync/events", new SyncPushRequest([first]))).Content.ReadFromJsonAsync<SyncPushResult>())!;
        Assert.Equal([first.EventId], retry.Accepted);

        var report = (await owner.GetFromJsonAsync<DailyReportDto>("/api/reports/daily?date=2026-10-27"))!;
        Assert.Equal(1, report.OrderCount);
        Assert.Equal(46000, report.SalesPaisa);
        Assert.Equal(2000, report.DiscountPaisa);
        Assert.Equal(46000, report.ByMethod["cash"]);
        Assert.Equal("Croissant", report.TopItems[0].Name);
        Assert.Equal(2, report.TopItems[0].Quantity);
        Assert.NotNull(report.LastSyncAt);
    }

    [Fact]
    public async Task Server_rejects_orders_whose_amounts_do_not_add_up()
    {
        var (_, till, _) = await RegisterTillAsync();
        var bad = OrderEvent(cash: 40000);
        var good = OrderEvent(receipt: "TV-20261027-0002");

        var result = (await (await till.PostAsJsonAsync("/api/sync/events", new SyncPushRequest([bad, good]))).Content.ReadFromJsonAsync<SyncPushResult>())!;
        Assert.Equal([good.EventId], result.Accepted);
        var rejection = Assert.Single(result.Rejected);
        Assert.Equal(bad.EventId, rejection.EventId);
        Assert.Contains("Payments do not add up", rejection.Reason);
    }

    [Fact]
    public async Task Reused_event_id_with_different_content_is_rejected()
    {
        var (_, till, _) = await RegisterTillAsync();
        var id = Guid.NewGuid();
        await till.PostAsJsonAsync("/api/sync/events", new SyncPushRequest([OrderEvent(eventId: id)]));
        var tampered = OrderEvent(eventId: id, receipt: "TV-20261027-0009");

        var result = (await (await till.PostAsJsonAsync("/api/sync/events", new SyncPushRequest([tampered]))).Content.ReadFromJsonAsync<SyncPushResult>())!;
        Assert.Empty(result.Accepted);
        Assert.Contains("different content", Assert.Single(result.Rejected).Reason);
    }

    [Fact]
    public async Task Only_valid_unrevoked_till_keys_can_upload()
    {
        var (owner, till, device) = await RegisterTillAsync();
        var body = new SyncPushRequest([OrderEvent()]);

        var noKey = await _app.CreateClient().PostAsJsonAsync("/api/sync/events", body);
        Assert.Equal(HttpStatusCode.Unauthorized, noKey.StatusCode);

        var wrong = _app.CreateClient();
        wrong.DefaultRequestHeaders.Add("X-Device-Key", $"{device.Device.Id}.not-the-secret");
        Assert.Equal(HttpStatusCode.Unauthorized, (await wrong.PostAsJsonAsync("/api/sync/events", body)).StatusCode);

        // A staff login token is not a till key.
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.PostAsJsonAsync("/api/sync/events", body)).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/devices/{device.Device.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await till.PostAsJsonAsync("/api/sync/events", body)).StatusCode);

        var listed = (await owner.GetFromJsonAsync<List<DeviceDto>>("/api/devices"))!;
        Assert.True(Assert.Single(listed).Revoked);
    }
}
