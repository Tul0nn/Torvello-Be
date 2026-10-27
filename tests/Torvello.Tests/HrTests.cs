using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Torvello.Application.Hr;
using Torvello.Application.Sync;
using Torvello.Domain;

namespace Torvello.Tests;

public sealed class HrTests : IDisposable
{
    private readonly TestApp _app = new();

    public void Dispose() => _app.Dispose();

    [Fact]
    public async Task Policies_are_versioned_and_staff_confirm_each_version()
    {
        var (owner, _) = await _app.OwnerAsync();
        var manager = await _app.StaffAsync(owner, "mgr@torvello.test", Roles.Manager);
        var cashier = await _app.StaffAsync(owner, "cash@torvello.test", Roles.Cashier);

        var draft = await manager.PostAsJsonAsync("/api/hr/policies", new PolicyInput("Uniform", "Conduct", "Wear the apron.", Published: false));
        Assert.Equal(HttpStatusCode.Created, draft.StatusCode);
        var policy = (await draft.Content.ReadFromJsonAsync<PolicyDto>())!;
        Assert.Equal(1, policy.Version);

        // Cashiers cannot see drafts or write policies.
        Assert.Empty((await cashier.GetFromJsonAsync<List<PolicyDto>>("/api/hr/policies"))!);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.GetAsync($"/api/hr/policies/{policy.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.PostAsJsonAsync("/api/hr/policies", new PolicyInput("X", "Y", "Z", true))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await cashier.PostAsync($"/api/hr/policies/{policy.Id}/acknowledge", null)).StatusCode);

        // Publishing does not create a new version.
        policy = (await (await manager.PutAsJsonAsync($"/api/hr/policies/{policy.Id}", new PolicyInput("Uniform", "Conduct", "Wear the apron.", true))).Content.ReadFromJsonAsync<PolicyDto>())!;
        Assert.Equal(1, policy.Version);

        Assert.Equal(HttpStatusCode.NoContent, (await cashier.PostAsync($"/api/hr/policies/{policy.Id}/acknowledge", null)).StatusCode);
        var seen = Assert.Single((await cashier.GetFromJsonAsync<List<PolicyDto>>("/api/hr/policies"))!);
        Assert.Equal(1, seen.AcknowledgedVersion);
        Assert.NotNull(seen.AcknowledgedAt);
        Assert.Equal("mgr", seen.UpdatedByName);

        // Changing the text asks everyone to confirm again.
        policy = (await (await manager.PutAsJsonAsync($"/api/hr/policies/{policy.Id}", new PolicyInput("Uniform", "Conduct", "Wear the apron and cap.", true))).Content.ReadFromJsonAsync<PolicyDto>())!;
        Assert.Equal(2, policy.Version);
        Assert.Equal(1, (await cashier.GetFromJsonAsync<PolicyDto>($"/api/hr/policies/{policy.Id}"))!.AcknowledgedVersion);

        var acks = (await owner.GetFromJsonAsync<List<PolicyAckDto>>($"/api/hr/policies/{policy.Id}/acknowledgements"))!;
        Assert.Equal(3, acks.Count);
        Assert.Equal(1, acks.Single(a => a.Email == "cash@torvello.test").AcknowledgedVersion);
        Assert.Null(acks.Single(a => a.Email == "mgr@torvello.test").AcknowledgedVersion);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync($"/api/hr/policies/{policy.Id}/acknowledgements")).StatusCode);

        // Only the owner deletes.
        Assert.Equal(HttpStatusCode.Forbidden, (await manager.DeleteAsync($"/api/hr/policies/{policy.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NoContent, (await owner.DeleteAsync($"/api/hr/policies/{policy.Id}")).StatusCode);
        Assert.Empty((await cashier.GetFromJsonAsync<List<PolicyDto>>("/api/hr/policies"))!);
    }

    [Fact]
    public async Task Invalid_policies_are_rejected()
    {
        var (owner, _) = await _app.OwnerAsync();
        var empty = await owner.PostAsJsonAsync("/api/hr/policies", new PolicyInput(" ", "Conduct", "Text", true));
        Assert.Equal(HttpStatusCode.BadRequest, empty.StatusCode);
        var huge = await owner.PostAsJsonAsync("/api/hr/policies", new PolicyInput("Big", "Conduct", new string('x', 50_001), true));
        Assert.Equal(HttpStatusCode.BadRequest, huge.StatusCode);
    }

    [Fact]
    public async Task Attendance_from_the_till_gives_hours_per_person()
    {
        var (owner, _) = await _app.OwnerAsync();
        var device = (await (await owner.PostAsJsonAsync("/api/devices", new CreateDeviceRequest("Counter"))).Content.ReadFromJsonAsync<CreatedDeviceDto>())!;
        var till = _app.CreateClient();
        till.DefaultRequestHeaders.Add("X-Device-Key", device.DeviceKey);

        SyncEventDto Ev(string type, object payload) => new(Guid.NewGuid(), type, DateTimeOffset.UtcNow, JsonSerializer.SerializeToElement(payload));
        var shiftA = Guid.NewGuid();
        var shiftB = Guid.NewGuid();
        var open = Guid.NewGuid();
        var result = (await (await till.PostAsJsonAsync("/api/sync/events", new SyncPushRequest(
        [
            Ev("attendance_clock_in", new { id = shiftA.ToString(), employeeEmail = "Rahim@torvello.test", employeeName = "Rahim", businessDate = "2026-10-26", at = "2026-10-26T03:00:00Z" }),
            Ev("attendance_clock_out", new { id = shiftA.ToString(), at = "2026-10-26T11:15:00Z" }),
            Ev("attendance_clock_in", new { id = shiftB.ToString(), employeeEmail = "rahim@torvello.test", employeeName = "Rahim", businessDate = "2026-10-27", at = "2026-10-27T03:00:00Z" }),
            Ev("attendance_clock_out", new { id = shiftB.ToString(), at = "2026-10-27T07:30:00Z" }),
            Ev("attendance_clock_in", new { id = open.ToString(), employeeEmail = "karim@torvello.test", employeeName = "Karim", businessDate = "2026-10-27", at = "2026-10-27T04:00:00Z" }),
            Ev("attendance_clock_out", new { id = open.ToString(), at = "2026-10-27T03:00:00Z" }),
            Ev("attendance_clock_out", new { id = Guid.NewGuid().ToString(), at = "2026-10-27T05:00:00Z" }),
        ]))).Content.ReadFromJsonAsync<SyncPushResult>())!;
        Assert.Equal(5, result.Accepted.Count);
        Assert.Contains(result.Rejected, r => r.Reason.Contains("before clock-in"));
        Assert.Contains(result.Rejected, r => r.Reason.Contains("was not received"));

        var report = (await owner.GetFromJsonAsync<AttendanceReportDto>("/api/hr/attendance?from=2026-10-20&to=2026-10-27"))!;
        Assert.Equal(3, report.Records.Count);
        var rahim = report.Totals.Single(t => t.EmployeeEmail == "rahim@torvello.test");
        Assert.Equal(2, rahim.Days);
        Assert.Equal(8 * 60 + 15 + 4 * 60 + 30, rahim.Minutes);
        Assert.False(rahim.Open);
        var karim = report.Totals.Single(t => t.EmployeeName == "Karim");
        Assert.True(karim.Open);
        Assert.Null(report.Records.Single(r => r.Id == open).Minutes);

        Assert.Equal(HttpStatusCode.BadRequest, (await owner.GetAsync("/api/hr/attendance?from=2026-01-01&to=2026-10-27")).StatusCode);
        var cashier = await _app.StaffAsync(owner, "cash@torvello.test", Roles.Cashier);
        Assert.Equal(HttpStatusCode.Forbidden, (await cashier.GetAsync("/api/hr/attendance")).StatusCode);
    }
}
