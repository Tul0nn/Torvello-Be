using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Torvello.Application.Common;
using Torvello.Application.People;
using Torvello.Domain;
using Torvello.Infrastructure.Persistence;
using Torvello.Infrastructure.Sync;

namespace Torvello.Infrastructure.People;

public sealed class CustomerService(AppDbContext db, ChangeFeed changes, TimeProvider clock) : ICustomerService
{
    private const int RecentOrderLimit = 50;

    public async Task<CustomerPageDto> ListAsync(CustomerQuery query, bool fullPhone, CancellationToken ct)
    {
        var page = Math.Max(1, query.Page);
        var size = Math.Clamp(query.PageSize, 1, 100);
        var customers = db.Customers.AsNoTracking();

        var search = query.Search?.Trim();
        if (!string.IsNullOrEmpty(search))
        {
            var lower = search.ToLowerInvariant();
            // "01712..." should match the stored "+8801712...": drop a leading 0 from typed numbers.
            var phonePart = new string(search.Where(char.IsAsciiDigit).ToArray()).TrimStart('0');
            customers = customers.Where(c =>
                (c.Name != null && c.Name.ToLower().Contains(lower)) || (phonePart.Length >= 3 && c.Phone.Contains(phonePart)));
        }

        // One cafe has thousands of customers, not millions: totals are combined in memory.
        var rows = await customers.ToListAsync(ct);
        var stats = await StatsAsync(rows.Select(c => c.Id).ToList(), ct);
        var summaries = rows.Select(c => Summary(c, stats.GetValueOrDefault(c.Id), fullPhone));
        summaries = query.Sort switch
        {
            "spend" => summaries.OrderByDescending(s => s.TotalSpentPaisa),
            "visits" => summaries.OrderByDescending(s => s.Visits),
            "name" => summaries.OrderBy(s => s.Name ?? "￿", StringComparer.OrdinalIgnoreCase),
            _ => summaries.OrderByDescending(s => s.LastVisitAt ?? DateTimeOffset.MinValue),
        };
        var ordered = summaries.ToList();
        return new CustomerPageDto(ordered.Skip((page - 1) * size).Take(size).ToList(), ordered.Count, page, size);
    }

    public async Task<CustomerDetailDto> GetAsync(Guid id, bool fullPhone, CancellationToken ct)
    {
        var customer = await db.Customers.AsNoTracking().SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound("Customer not found");
        return await DetailAsync(customer, fullPhone, ct);
    }

    public async Task<CustomerDetailDto> UpdateAsync(string editorName, Guid id, CustomerUpdate update, CancellationToken ct)
    {
        var customer = await db.Customers.SingleOrDefaultAsync(c => c.Id == id, ct) ?? throw AppException.NotFound("Customer not found");
        customer.Name = Clean(update.Name);
        customer.BirthdayMonth = update.BirthdayMonth;
        customer.BirthdayDay = update.BirthdayDay;
        customer.AnniversaryMonth = update.AnniversaryMonth;
        customer.AnniversaryDay = update.AnniversaryDay;
        customer.Notes = Clean(update.Notes);
        customer.MarketingConsent = update.MarketingConsent;
        customer.UpdatedAt = clock.GetUtcNow();
        customer.UpdatedByName = editorName;
        changes.Add("customer", customer.Id, CustomerProjection.ToPayload(customer));
        await db.SaveChangesAsync(ct);
        return await DetailAsync(customer, fullPhone: true, ct);
    }

    private async Task<CustomerDetailDto> DetailAsync(Customer c, bool fullPhone, CancellationToken ct)
    {
        var stats = (await StatsAsync([c.Id], ct)).GetValueOrDefault(c.Id);
        var orders = await db.SyncedOrders.AsNoTracking()
            .Where(o => o.CustomerId == c.Id)
            .OrderByDescending(o => o.CompletedAtMs)
            .Take(RecentOrderLimit)
            .Select(o => new CustomerOrderDto(o.Id, o.ReceiptNumber, o.CompletedAt, o.TotalPaisa, o.Lines.Sum(l => l.Quantity)))
            .ToListAsync(ct);
        var s = Summary(c, stats, fullPhone);
        return new CustomerDetailDto(s.Id, s.Phone, s.Name, s.Visits, s.TotalSpentPaisa, s.LastVisitAt, c.BirthdayMonth, c.BirthdayDay,
            c.AnniversaryMonth, c.AnniversaryDay, c.Notes, c.MarketingConsent, c.CreatedAt, c.UpdatedAt, c.UpdatedByName, orders);
    }

    private sealed record Stats(int Visits, long Spent, long LastMs);

    private async Task<Dictionary<Guid, Stats>> StatsAsync(List<Guid> ids, CancellationToken ct) =>
        await db.SyncedOrders.AsNoTracking()
            .Where(o => o.CustomerId != null && ids.Contains(o.CustomerId.Value))
            .GroupBy(o => o.CustomerId!.Value)
            .Select(g => new { Id = g.Key, Visits = g.Count(), Spent = g.Sum(o => o.TotalPaisa), LastMs = g.Max(o => o.CompletedAtMs) })
            .ToDictionaryAsync(x => x.Id, x => new Stats(x.Visits, x.Spent, x.LastMs), ct);

    private static CustomerSummaryDto Summary(Customer c, Stats? s, bool fullPhone) =>
        new(c.Id, fullPhone ? c.Phone : PhoneNumbers.Mask(c.Phone), c.Name, s?.Visits ?? 0, s?.Spent ?? 0,
            s is null ? null : DateTimeOffset.FromUnixTimeMilliseconds(s.LastMs), c.BirthdayMonth, c.BirthdayDay);

    private static string? Clean(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();
}

/// <summary>Customers saved at a till, and the customer on each sale. The newest edit (till or website) wins.</summary>
public static class CustomerProjection
{
    public static async Task CustomerSavedAsync(AppDbContext db, JsonElement p, DateTimeOffset eventAt, CancellationToken ct)
    {
        var phone = PhoneNumbers.Normalize(Str(p, "phone")) ?? throw new SyncRejection("Customer phone is not a Bangladeshi mobile number");
        var name = Text(p, "name", 60);
        var notes = Text(p, "notes", 1000);
        var birthdayMonth = Int(p, "birthdayMonth");
        var birthdayDay = Int(p, "birthdayDay");
        var anniversaryMonth = Int(p, "anniversaryMonth");
        var anniversaryDay = Int(p, "anniversaryDay");
        if (!CalendarDays.IsValid(birthdayMonth, birthdayDay)) throw new SyncRejection("Birthday is not a real date");
        if (!CalendarDays.IsValid(anniversaryMonth, anniversaryDay)) throw new SyncRejection("Anniversary is not a real date");
        var updatedAt = DateTimeOffset.TryParse(Str(p, "updatedAt"), out var u) ? u : eventAt;

        var customer = await FindOrCreateAsync(db, phone, updatedAt, ct);
        if (Guid.TryParse(Str(p, "id"), out var tillId)) customer.TillCustomerId ??= tillId;
        // An older till edit must not overwrite a newer one made on the website.
        if (customer.UpdatedByName is not null && updatedAt < customer.UpdatedAt) return;
        customer.Name = name;
        customer.BirthdayMonth = birthdayMonth;
        customer.BirthdayDay = birthdayDay;
        customer.AnniversaryMonth = anniversaryMonth;
        customer.AnniversaryDay = anniversaryDay;
        customer.Notes = notes;
        customer.MarketingConsent = p.TryGetProperty("marketingConsent", out var m) && m.ValueKind == JsonValueKind.True;
        customer.UpdatedAt = updatedAt;
        customer.UpdatedByName = Text(p, "updatedByName", 60);
    }

    /// <summary>Links a projected order to its customer when the till recorded a phone number.</summary>
    public static async Task LinkOrderAsync(AppDbContext db, JsonElement payload, SyncedOrder order, CancellationToken ct)
    {
        var raw = Str(payload, "customerPhone");
        if (string.IsNullOrEmpty(raw)) return;
        var phone = PhoneNumbers.Normalize(raw) ?? throw new SyncRejection("Customer phone is not a Bangladeshi mobile number");
        order.CustomerId = (await FindOrCreateAsync(db, phone, order.CompletedAt, ct)).Id;
    }

    /// <summary>What a till needs to apply a website edit.</summary>
    public static object ToPayload(Customer c) => new
    {
        id = c.TillCustomerId,
        phone = c.Phone,
        name = c.Name,
        birthdayMonth = c.BirthdayMonth,
        birthdayDay = c.BirthdayDay,
        anniversaryMonth = c.AnniversaryMonth,
        anniversaryDay = c.AnniversaryDay,
        notes = c.Notes,
        marketingConsent = c.MarketingConsent,
        updatedAt = c.UpdatedAt,
        updatedByName = c.UpdatedByName,
    };

    private static async Task<Customer> FindOrCreateAsync(AppDbContext db, string phone, DateTimeOffset at, CancellationToken ct)
    {
        var customer = db.Customers.Local.FirstOrDefault(c => c.Phone == phone) ?? await db.Customers.FirstOrDefaultAsync(c => c.Phone == phone, ct);
        if (customer is not null) return customer;
        customer = new Customer { Phone = phone, CreatedAt = at, UpdatedAt = at };
        db.Customers.Add(customer);
        return customer;
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;

    private static string? Text(JsonElement e, string name, int max)
    {
        var text = Str(e, name)?.Trim();
        if (string.IsNullOrEmpty(text)) return null;
        return text.Length <= max ? text : throw new SyncRejection($"{name} is too long");
    }
}
