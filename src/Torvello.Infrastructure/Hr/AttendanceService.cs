using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Torvello.Application.Common;
using Torvello.Application.Hr;
using Torvello.Infrastructure.Persistence;
using Torvello.Infrastructure.Sync;

namespace Torvello.Infrastructure.Hr;

public sealed class AttendanceService(AppDbContext db, TimeProvider clock) : IAttendanceService
{
    private const int MaxDays = 62;

    public async Task<AttendanceReportDto> ReportAsync(DateOnly? from, DateOnly? to, CancellationToken ct)
    {
        var end = to ?? BusinessTime.Today(clock);
        var start = from ?? end.AddDays(-6);
        if (start > end) throw AppException.BadRequest("The start date must be before the end date");
        if (end.DayNumber - start.DayNumber >= MaxDays) throw AppException.BadRequest($"Choose at most {MaxDays} days");

        var rows = await db.AttendanceRecords.AsNoTracking()
            .Where(a => a.BusinessDate >= start && a.BusinessDate <= end)
            .OrderByDescending(a => a.ClockInMs)
            .ToListAsync(ct);

        var records = rows
            .Select(a => new AttendanceRecordDto(a.Id, a.EmployeeName, a.EmployeeEmail, a.BusinessDate, a.ClockInAt, a.ClockOutAt, Minutes(a)))
            .ToList();
        var totals = rows
            .GroupBy(a => a.EmployeeEmail)
            .Select(g => new AttendanceTotalDto(
                g.Key,
                g.First().EmployeeName,
                g.Select(a => a.BusinessDate).Distinct().Count(),
                g.Sum(a => Minutes(a) ?? 0),
                g.Any(a => a.ClockOutAt is null)))
            .OrderBy(t => t.EmployeeName, StringComparer.OrdinalIgnoreCase)
            .ToList();
        return new AttendanceReportDto(start, end, records, totals);
    }

    private static int? Minutes(AttendanceRecord a) => a.ClockOutAt is { } outAt ? (int)Math.Floor((outAt - a.ClockInAt).TotalMinutes) : null;
}

/// <summary>Applies clock-in / clock-out events from a till.</summary>
public static class AttendanceProjection
{
    public static void ClockIn(AppDbContext db, JsonElement payload, Guid deviceId)
    {
        var id = Guid.TryParse(Str(payload, "id"), out var g) ? g : throw new SyncRejection("id is missing");
        var email = Str(payload, "employeeEmail")?.Trim().ToLowerInvariant();
        if (string.IsNullOrEmpty(email) || email.Length > 254) throw new SyncRejection("employeeEmail is missing");
        var name = Str(payload, "employeeName")?.Trim();
        if (string.IsNullOrEmpty(name) || name.Length > 60) throw new SyncRejection("employeeName is missing");
        var at = Date(payload, "at") ?? throw new SyncRejection("at is missing");
        var businessDate = DateOnly.TryParseExact(Str(payload, "businessDate"), "yyyy-MM-dd", out var d)
            ? d
            : DateOnly.FromDateTime(BusinessTime.ToDhaka(at).DateTime);

        db.AttendanceRecords.Add(new AttendanceRecord
        {
            Id = id,
            DeviceId = deviceId,
            EmployeeEmail = email,
            EmployeeName = name,
            BusinessDate = businessDate,
            ClockInAt = at,
            ClockInMs = at.ToUnixTimeMilliseconds(),
        });
    }

    public static async Task ClockOutAsync(AppDbContext db, JsonElement payload, Guid deviceId, CancellationToken ct)
    {
        var id = Guid.TryParse(Str(payload, "id"), out var g) ? g : throw new SyncRejection("id is missing");
        var at = Date(payload, "at") ?? throw new SyncRejection("at is missing");
        var record = await db.AttendanceRecords.SingleOrDefaultAsync(a => a.Id == id, ct) ?? throw new SyncRejection("The clock-in for this clock-out was not received");
        if (record.DeviceId != deviceId) throw new SyncRejection("This attendance record belongs to another till");
        if (at < record.ClockInAt) throw new SyncRejection("Clock-out is before clock-in");
        record.ClockOutAt = at;
        var closedBy = Str(payload, "closedBy")?.Trim();
        record.ClosedBy = string.IsNullOrEmpty(closedBy) ? null : closedBy[..Math.Min(closedBy.Length, 60)];
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static DateTimeOffset? Date(JsonElement e, string name) => DateTimeOffset.TryParse(Str(e, name), out var at) ? at : null;
}
