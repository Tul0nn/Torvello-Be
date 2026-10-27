using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Torvello.Application.Common;
using Torvello.Application.People;
using Torvello.Domain;
using Torvello.Infrastructure.Persistence;
using Torvello.Infrastructure.Sync;

namespace Torvello.Infrastructure.People;

public sealed class EmployeeService(AppDbContext db, IDataProtectionProvider protection, ChangeFeed changes, TimeProvider clock) : IEmployeeService
{
    private readonly IDataProtector _nationalId = NationalIds.Protector(protection);

    public async Task<EmployeeProfileDto> UpdateAsync(string editorName, Guid id, EmployeeProfileUpdate update, CancellationToken ct)
    {
        var staff = await db.StaffRecords.SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound("Staff member not found");
        var nationalId = string.IsNullOrWhiteSpace(update.NationalId) ? null : update.NationalId.Replace(" ", "");
        staff.Position = Clean(update.Position);
        staff.Phone = OptionalPhone(update.Phone, "Phone");
        staff.JoinDate = update.JoinDate;
        staff.DateOfBirth = update.DateOfBirth;
        staff.Address = Clean(update.Address);
        staff.NationalIdProtected = nationalId is null ? null : _nationalId.Protect(nationalId);
        staff.EmergencyContactName = Clean(update.EmergencyContactName);
        staff.EmergencyContactPhone = OptionalPhone(update.EmergencyContactPhone, "Emergency contact phone");
        staff.Notes = Clean(update.Notes);
        staff.UpdatedAt = clock.GetUtcNow();
        staff.UpdatedByName = editorName;
        changes.Add("staff", staff.Id, StaffProjection.ToPayload(staff, nationalId));
        await db.SaveChangesAsync(ct);
        return await ToDtoAsync(staff, canSeeSensitive: true, ct);
    }

    private static string? OptionalPhone(string? input, string label) =>
        string.IsNullOrWhiteSpace(input)
            ? null
            : PhoneNumbers.Normalize(input) ?? throw AppException.BadRequest($"{label} must be a Bangladeshi mobile number, e.g. 01712345678");

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    public async Task<IReadOnlyList<EmployeeSummaryDto>> ListAsync(CancellationToken ct)
    {
        var staff = await db.StaffRecords.AsNoTracking().ToListAsync(ct);
        var context = await ContextAsync(staff.Select(s => s.Email), ct);
        return staff
            .OrderByDescending(s => s.Active)
            .ThenBy(s => s.Name, StringComparer.OrdinalIgnoreCase)
            .Select(s => new EmployeeSummaryDto(s.Id, s.Name, s.Email, RoleLabel(s.Role), s.Active, s.Position, s.Phone, s.JoinDate,
                context.Accounts.ContainsKey(s.Email), context.OnShift.Contains(s.Email)))
            .ToList();
    }

    public async Task<EmployeeProfileDto> GetAsync(Viewer viewer, Guid id, CancellationToken ct)
    {
        var staff = await db.StaffRecords.AsNoTracking().SingleOrDefaultAsync(s => s.Id == id, ct) ?? throw AppException.NotFound("Staff member not found");
        return await ToDtoAsync(staff, canSeeSensitive: viewer.IsOwner, ct);
    }

    public async Task<EmployeeProfileDto> MeAsync(Viewer viewer, CancellationToken ct)
    {
        var email = viewer.Email.ToLowerInvariant();
        var staff = await db.StaffRecords.AsNoTracking().Where(s => s.Email == email).ToListAsync(ct);
        var mine = staff.OrderByDescending(s => s.Active).ThenByDescending(s => s.UpdatedAt).FirstOrDefault()
            ?? throw AppException.NotFound("Your staff details have not been added on the till yet.");
        // Everyone may see their own personal details, but not the owner's private notes.
        var dto = await ToDtoAsync(mine, canSeeSensitive: true, ct);
        return viewer.IsOwner ? dto : dto with { Notes = null };
    }

    private sealed record Context(Dictionary<string, Guid> Accounts, HashSet<string> OnShift);

    /// <summary>Website logins and current clock-ins for these emails.</summary>
    private async Task<Context> ContextAsync(IEnumerable<string> emails, CancellationToken ct)
    {
        var wanted = emails.Select(e => e.ToUpperInvariant()).Distinct().ToList();
        var accounts = await db.Users.AsNoTracking()
            .Where(u => u.NormalizedEmail != null && wanted.Contains(u.NormalizedEmail))
            .Select(u => new { u.Email, u.Id })
            .ToListAsync(ct);
        var open = await db.AttendanceRecords.AsNoTracking().Where(a => a.ClockOutAt == null).Select(a => a.EmployeeEmail).ToListAsync(ct);
        return new Context(accounts.ToDictionary(a => a.Email!.ToLowerInvariant(), a => a.Id), [.. open]);
    }

    private async Task<EmployeeProfileDto> ToDtoAsync(StaffRecord s, bool canSeeSensitive, CancellationToken ct)
    {
        var context = await ContextAsync([s.Email], ct);
        var today = BusinessTime.Today(clock);
        var from = today.AddDays(-29);
        var attendance = await db.AttendanceRecords.AsNoTracking()
            .Where(a => a.EmployeeEmail == s.Email && a.BusinessDate >= from && a.BusinessDate <= today)
            .ToListAsync(ct);
        var minutes = attendance.Where(a => a.ClockOutAt is not null).Sum(a => (int)Math.Floor((a.ClockOutAt!.Value - a.ClockInAt).TotalMinutes));
        var days = attendance.Select(a => a.BusinessDate).Distinct().Count();

        PolicySummary? policies = null;
        if (context.Accounts.TryGetValue(s.Email, out var userId))
        {
            var published = await db.Policies.AsNoTracking().Where(p => p.Published && !p.Deleted).Select(p => new { p.Id, p.Version }).ToListAsync(ct);
            var acks = await db.PolicyAcknowledgements.AsNoTracking().Where(a => a.UserId == userId).ToDictionaryAsync(a => a.PolicyId, a => a.Version, ct);
            policies = new PolicySummary(published.Count(p => acks.TryGetValue(p.Id, out var v) && v == p.Version), published.Count);
        }

        return new EmployeeProfileDto(
            s.Id, s.Name, s.Email, RoleLabel(s.Role), s.Active, s.Position, s.Phone, s.JoinDate,
            context.Accounts.ContainsKey(s.Email), context.OnShift.Contains(s.Email),
            canSeeSensitive ? s.DateOfBirth : null,
            canSeeSensitive ? s.Address : null,
            canSeeSensitive ? NationalIds.Reveal(_nationalId, s.NationalIdProtected) : null,
            s.EmergencyContactName, s.EmergencyContactPhone,
            canSeeSensitive ? s.Notes : null,
            canSeeSensitive, s.UpdatedAt, s.UpdatedByName,
            new AttendanceSummary(days, minutes),
            policies);
    }

    /// <summary>Till roles are lower case ("manager"); the website shows "Manager".</summary>
    private static string RoleLabel(string role) => CultureInfo.InvariantCulture.TextInfo.ToTitleCase(role);
}

public static class NationalIds
{
    public static IDataProtector Protector(IDataProtectionProvider provider) => provider.CreateProtector("Torvello.Staff.NationalId.v1");

    public static string? Reveal(IDataProtector protector, string? stored)
    {
        if (stored is null) return null;
        try
        {
            return protector.Unprotect(stored);
        }
        catch (System.Security.Cryptography.CryptographicException)
        {
            return "(cannot be read: encryption keys changed)";
        }
    }
}

/// <summary>Staff details saved on a till (employee_saved events).</summary>
public static class StaffProjection
{
    private static readonly HashSet<string> Roles = ["owner", "manager", "cashier"];

    public static async Task EmployeeSavedAsync(AppDbContext db, IDataProtector nationalIds, JsonElement p, Guid deviceId, DateTimeOffset at, CancellationToken ct)
    {
        var id = Guid.TryParse(Str(p, "id"), out var g) ? g : throw new SyncRejection("id is missing");
        var name = Text(p, "name", 60) ?? throw new SyncRejection("name is missing");
        var email = Text(p, "email", 254)?.ToLowerInvariant() ?? throw new SyncRejection("email is missing");
        var role = Str(p, "role");
        if (role is null || !Roles.Contains(role)) throw new SyncRejection("Unknown role");

        var phone = OptionalPhone(Str(p, "phone"));
        var emergencyPhone = OptionalPhone(Str(p, "emergencyContactPhone"));
        var nationalId = Str(p, "nationalId")?.Replace(" ", "");
        if (!string.IsNullOrEmpty(nationalId) && nationalId.Length is not (10 or 13 or 17) | !nationalId.All(char.IsAsciiDigit))
        {
            throw new SyncRejection("National ID must be 10, 13 or 17 digits");
        }

        var updatedAt = DateTimeOffset.TryParse(Str(p, "updatedAt"), out var u) ? u : at;
        var staff = await db.StaffRecords.FindAsync([id], ct);
        if (staff is null)
        {
            staff = new StaffRecord { Id = id, DeviceId = deviceId };
            db.StaffRecords.Add(staff);
        }
        else if (staff.DeviceId != deviceId)
        {
            throw new SyncRejection("This staff record belongs to another till");
        }
        // Account fields (name, email, role, active) always come from the till.
        staff.Name = name;
        staff.Email = email;
        staff.Role = role;
        staff.Active = !p.TryGetProperty("active", out var a) || a.ValueKind != JsonValueKind.False;
        // HR details: an older till edit must not overwrite a newer one made on the website.
        if (staff.UpdatedByName is not null && updatedAt < staff.UpdatedAt) return;
        staff.Position = Text(p, "position", 60);
        staff.Phone = phone;
        staff.JoinDate = Date(p, "joinDate");
        staff.DateOfBirth = Date(p, "dateOfBirth");
        staff.Address = Text(p, "address", 300);
        staff.NationalIdProtected = string.IsNullOrEmpty(nationalId) ? null : nationalIds.Protect(nationalId);
        staff.EmergencyContactName = Text(p, "emergencyContactName", 60);
        staff.EmergencyContactPhone = emergencyPhone;
        staff.Notes = Text(p, "notes", 1000);
        staff.UpdatedAt = updatedAt;
        staff.UpdatedByName = Text(p, "updatedByName", 60);
    }

    /// <summary>What a till needs to apply a website edit of HR details (account fields stay with the till).</summary>
    public static object ToPayload(StaffRecord s, string? nationalId) => new
    {
        id = s.Id,
        position = s.Position,
        phone = s.Phone,
        joinDate = s.JoinDate?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        dateOfBirth = s.DateOfBirth?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
        address = s.Address,
        nationalId,
        emergencyContactName = s.EmergencyContactName,
        emergencyContactPhone = s.EmergencyContactPhone,
        notes = s.Notes,
        updatedAt = s.UpdatedAt,
        updatedByName = s.UpdatedByName,
    };

    private static string? OptionalPhone(string? raw) =>
        string.IsNullOrWhiteSpace(raw) ? null : PhoneNumbers.Normalize(raw) ?? throw new SyncRejection("Phone is not a Bangladeshi mobile number");

    private static DateOnly? Date(JsonElement e, string name)
    {
        var raw = Str(e, name);
        if (string.IsNullOrEmpty(raw)) return null;
        return DateOnly.TryParseExact(raw, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d) ? d : throw new SyncRejection($"{name} is not a date");
    }

    private static string? Text(JsonElement e, string name, int max)
    {
        var text = Str(e, name)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= max ? text : throw new SyncRejection($"{name} is too long");
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
