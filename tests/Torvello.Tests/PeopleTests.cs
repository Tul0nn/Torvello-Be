using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Torvello.Application.People;
using Torvello.Application.Sync;
using Torvello.Domain;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Tests;

public sealed class PeopleTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Theory]
    [InlineData("01712345678", "+8801712345678")]
    [InlineData("+880 1712-345678", "+8801712345678")]
    [InlineData("8801912345678", "+8801912345678")]
    [InlineData("01212345678", null)]
    [InlineData("0171234567", null)]
    [InlineData("hello", null)]
    public void Bangladeshi_mobile_numbers_are_normalised(string input, string? expected) =>
        Assert.Equal(expected, PhoneNumbers.Normalize(input));

    private static SyncEventDto Ev(string type, object payload) =>
        new(Guid.NewGuid(), type, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(payload));

    private static SyncEventDto Sale(string receipt, long total, string? phone, string at) =>
        new(Guid.NewGuid(), "order_completed", DateTimeOffset.Parse(at), JsonSerializer.SerializeToElement(new
        {
            orderId = Guid.NewGuid().ToString(),
            receiptNumber = receipt,
            completedAt = at,
            orderType = "dine_in",
            employeeName = "Rahim",
            totals = new { subtotalPaisa = total + 1000, discountPaisa = 1000, totalPaisa = total },
            lines = new object[] { new { name = "Latte", unitPricePaisa = total + 1000, quantity = 1 } },
            payments = new object[] { new { method = "cash", amountPaisa = total } },
            customerPhone = phone,
        }));

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
    public async Task Customers_bills_and_owner_edits_flow_both_ways_with_editor_names()
    {
        var (owner, till) = await SetupAsync();
        var tillCustomerId = Guid.NewGuid();
        var result = await PushAsync(till,
            Ev("customer_saved", new
            {
                id = tillCustomerId.ToString(), phone = "01712345678", name = "Nadia", birthdayMonth = 3, birthdayDay = 2,
                notes = "Oat milk", marketingConsent = true, updatedAt = "2026-10-25T04:00:00Z", updatedByName = "Karim",
            }),
            Sale("TV-20261025-0001", 28000, "01712345678", "2026-10-25T05:00:00Z"),
            Sale("TV-20261027-0001", 32000, "+8801712345678", "2026-10-27T05:00:00Z"),
            Sale("TV-20261027-0002", 18000, "01812345678", "2026-10-27T06:00:00Z"),
            Sale("TV-20261027-0003", 9000, null, "2026-10-27T07:00:00Z"),
            Sale("TV-20261027-0004", 9000, "12345", "2026-10-27T08:00:00Z"),
            Ev("customer_saved", new { id = Guid.NewGuid().ToString(), phone = "01712345678", birthdayMonth = 2, birthdayDay = 30 }));
        Assert.Equal(5, result.Accepted.Count);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("Bangladeshi mobile"));
        Assert.Contains(result.Rejected, r => r.Reason.Contains("Birthday"));

        var page = (await owner.GetFromJsonAsync<CustomerPageDto>("/api/customers?sort=spend"))!;
        Assert.Equal(2, page.Total);
        var nadia = page.Items[0];
        Assert.Equal(("Nadia", "+8801712345678", 2, 60000L), (nadia.Name, nadia.Phone, nadia.Visits, nadia.TotalSpentPaisa));
        Assert.Equal(nadia.Id, Assert.Single((await owner.GetFromJsonAsync<CustomerPageDto>("/api/customers?search=0171234"))!.Items).Id);

        var detail = (await owner.GetFromJsonAsync<CustomerDetailDto>($"/api/customers/{nadia.Id}"))!;
        Assert.Equal("Karim", detail.UpdatedByName);
        Assert.Equal("TV-20261027-0001", detail.RecentOrders[0].ReceiptNumber);

        // Bills for a day, with the customer; managers get masked phones and cannot edit.
        var manager = await _app.StaffAsync(owner, "mgr@torvello.test", Roles.Manager);
        var bills = (await manager.GetFromJsonAsync<List<BillSummaryDto>>("/api/orders?date=2026-10-27"))!;
        Assert.Equal(3, bills.Count); // the sale with the invalid phone was refused
        Assert.Equal("TV-20261027-0003", bills[0].ReceiptNumber);
        var nadiaBill = bills.Single(b => b.ReceiptNumber == "TV-20261027-0001");
        Assert.Equal(("Nadia", "+8801******678"), (nadiaBill.CustomerName, nadiaBill.CustomerPhone));
        var bill = (await manager.GetFromJsonAsync<BillDto>($"/api/orders/{nadiaBill.Id}"))!;
        Assert.Equal((33000L, 1000L, 32000L), (bill.SubtotalPaisa, bill.DiscountPaisa, bill.TotalPaisa));
        Assert.Equal("cash", Assert.Single(bill.Payments).Method);
        var update = new CustomerUpdate("Nadia Rahman", 3, 2, 12, 1, "Oat milk, no sugar", true);
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync($"/api/customers/{nadia.Id}", update)).StatusCode);

        // The owner edits on the website: recorded with their name and put in the till download feed.
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/customers/{nadia.Id}", update with { BirthdayDay = 31, BirthdayMonth = 4 })).StatusCode);
        var edited = (await (await owner.PutAsJsonAsync($"/api/customers/{nadia.Id}", update)).Content.ReadFromJsonAsync<CustomerDetailDto>())!;
        Assert.Equal(("Nadia Rahman", "Owner"), (edited.Name, edited.UpdatedByName));
        var feed = (await till.GetFromJsonAsync<ServerChangesDto>("/api/sync/changes?after=0"))!;
        var change = Assert.Single(feed.Changes);
        Assert.Equal("customer", change.EntityType);
        Assert.Equal(tillCustomerId.ToString(), change.Payload.GetProperty("id").GetString());
        Assert.Equal("Owner", change.Payload.GetProperty("updatedByName").GetString());
        Assert.Empty((await till.GetFromJsonAsync<ServerChangesDto>($"/api/sync/changes?after={feed.Cursor}"))!.Changes);

        // A till edit made before the owner's edit (uploaded late) does not overwrite it; a later one does.
        await PushAsync(till, Ev("customer_saved", new { phone = "01712345678", name = "Old name", updatedAt = "2026-10-26T00:00:00Z", updatedByName = "Karim" }));
        Assert.Equal("Nadia Rahman", (await owner.GetFromJsonAsync<CustomerDetailDto>($"/api/customers/{nadia.Id}"))!.Name);
        await PushAsync(till, Ev("customer_saved", new { phone = "01712345678", name = "Nadia R.", updatedAt = DateTimeOffset.UtcNow.AddMinutes(1), updatedByName = "Karim" }));
        var latest = (await owner.GetFromJsonAsync<CustomerDetailDto>($"/api/customers/{nadia.Id}"))!;
        Assert.Equal(("Nadia R.", "Karim"), (latest.Name, latest.UpdatedByName));

        var cashier = await _app.StaffAsync(owner, "cash@torvello.test", Roles.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/customers")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/orders")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await owner.GetAsync("/api/sync/changes")).StatusCode);
    }

    [Fact]
    public async Task Staff_details_from_the_till_hide_personal_data_from_managers_and_encrypt_ids()
    {
        var (owner, till) = await SetupAsync();
        var manager = await _app.StaffAsync(owner, "mgr@torvello.test", Roles.Manager);
        var cashier = await _app.StaffAsync(owner, "cash@torvello.test", Roles.Cashier);
        var staffId = Guid.NewGuid();
        var result = await PushAsync(till,
            Ev("employee_saved", new
            {
                id = staffId.ToString(), name = "Rahim", email = "Cash@torvello.test", role = "cashier", active = true, position = "Barista",
                phone = "01712345678", joinDate = "2026-01-15", dateOfBirth = "2000-05-02", address = "Road 5, Dhanmondi", nationalId = "1234567890",
                emergencyContactName = "Mother", emergencyContactPhone = "01812345678", notes = "Very reliable",
                updatedAt = "2026-10-20T00:00:00Z", updatedByName = "Karim",
            }),
            Ev("employee_saved", new { id = Guid.NewGuid().ToString(), name = "X", email = "x@torvello.test", role = "cashier", nationalId = "123" }),
            Ev("attendance_clock_in", new { id = Guid.NewGuid().ToString(), employeeEmail = "cash@torvello.test", employeeName = "Rahim", at = DateTimeOffset.UtcNow.AddHours(-1) }));
        Assert.Equal(2, result.Accepted.Count);
        Assert.Contains("National ID", Assert.Single(result.Rejected).Reason);

        using (var scope = _app.Services.CreateScope())
        {
            var stored = await scope.ServiceProvider.GetRequiredService<AppDbContext>().StaffRecords.SingleAsync(s => s.Id == staffId);
            Assert.DoesNotContain("1234567890", stored.NationalIdProtected);
        }

        var list = (await manager.GetFromJsonAsync<List<EmployeeSummaryDto>>("/api/employees"))!;
        var rahim = Assert.Single(list);
        Assert.Equal(("Cashier", true, true, "+8801712345678"), (rahim.Role, rahim.HasWebsiteAccount, rahim.ClockedInNow, rahim.Phone));

        var asOwner = (await owner.GetFromJsonAsync<EmployeeProfileDto>($"/api/employees/{staffId}"))!;
        Assert.Equal(("1234567890", "Very reliable", "Karim"), (asOwner.NationalId, asOwner.Notes, asOwner.UpdatedByName));
        Assert.Equal(0, asOwner.Policies!.Total);

        var asManager = (await manager.GetFromJsonAsync<EmployeeProfileDto>($"/api/employees/{staffId}"))!;
        Assert.False(asManager.CanSeeSensitive);
        Assert.Null(asManager.NationalId);
        Assert.Null(asManager.Address);
        Assert.Equal("Barista", asManager.Position);

        var mine = (await cashier.GetFromJsonAsync<EmployeeProfileDto>("/api/employees/me"))!;
        Assert.Equal("1234567890", mine.NationalId);
        Assert.Null(mine.Notes);
        Assert.Equal(HttpStatusCode.NotFound, (await manager.GetAsync("/api/employees/me")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/employees/{staffId}")).StatusCode);

        // The owner corrects details on the website; the till gets them, national ID included, through the feed.
        var update = new EmployeeProfileUpdate("Senior barista", "01712345678", new DateOnly(2026, 1, 15), new DateOnly(2000, 5, 2),
            "Road 7, Dhanmondi", "1234567890123", "Mother", "01812345678", "Promoted");
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.PutAsJsonAsync($"/api/employees/{staffId}", update)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await owner.PutAsJsonAsync($"/api/employees/{staffId}", update with { Phone = "999" })).StatusCode);
        var edited = (await (await owner.PutAsJsonAsync($"/api/employees/{staffId}", update)).Content.ReadFromJsonAsync<EmployeeProfileDto>())!;
        Assert.Equal(("Senior barista", "Owner", "1234567890123"), (edited.Position, edited.UpdatedByName, edited.NationalId));
        var change = Assert.Single((await till.GetFromJsonAsync<ServerChangesDto>("/api/sync/changes?after=0"))!.Changes);
        Assert.Equal(("staff", "1234567890123"), (change.EntityType, change.Payload.GetProperty("nationalId").GetString()));

        // A late till upload of an older edit keeps the owner's HR details but still updates the account fields.
        await PushAsync(till, Ev("employee_saved", new
        {
            id = staffId.ToString(), name = "Rahim Uddin", email = "cash@torvello.test", role = "manager", position = "Barista", updatedAt = "2026-10-21T00:00:00Z",
        }));
        var after = (await owner.GetFromJsonAsync<EmployeeProfileDto>($"/api/employees/{staffId}"))!;
        Assert.Equal(("Rahim Uddin", "Manager", "Senior barista", "Owner"), (after.Name, after.Role, after.Position, after.UpdatedByName));
    }
}
