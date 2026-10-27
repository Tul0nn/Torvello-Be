using Microsoft.EntityFrameworkCore;
using Torvello.Application.Common;
using Torvello.Application.People;
using Torvello.Domain;
using Torvello.Infrastructure.Persistence;
using Torvello.Infrastructure.Sync;

namespace Torvello.Infrastructure.People;

/// <summary>Bills (completed sales) as uploaded by the tills. Read-only: corrections are refunds at the till.</summary>
public sealed class BillService(AppDbContext db, TimeProvider clock) : IBillService
{
    public async Task<IReadOnlyList<BillSummaryDto>> ListAsync(DateOnly? date, bool fullPhone, CancellationToken ct)
    {
        var day = date ?? BusinessTime.Today(clock);
        var orders = await db.SyncedOrders.AsNoTracking()
            .Where(o => o.BusinessDate == day)
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .AsSplitQuery()
            .OrderByDescending(o => o.CompletedAtMs)
            .ToListAsync(ct);
        var customers = await CustomersAsync(orders.Select(o => o.CustomerId), ct);
        return orders.Select(o => Summary(o, customers, fullPhone)).ToList();
    }

    public async Task<BillDto> GetAsync(Guid id, bool fullPhone, CancellationToken ct)
    {
        var o = await db.SyncedOrders.AsNoTracking()
            .Include(x => x.Lines)
            .Include(x => x.Payments)
            .AsSplitQuery()
            .SingleOrDefaultAsync(x => x.Id == id, ct) ?? throw AppException.NotFound("Bill not found");
        var s = Summary(o, await CustomersAsync([o.CustomerId], ct), fullPhone);
        return new BillDto(s.Id, s.ReceiptNumber, s.CompletedAt, s.OrderType, s.EmployeeName, s.CustomerId, s.CustomerName, s.CustomerPhone,
            s.ItemCount, s.TotalPaisa, s.Methods, o.SubtotalPaisa, o.DiscountPaisa,
            o.Lines.Select(l => new BillLineDto(l.Name, l.Quantity, l.UnitPricePaisa, l.LineTotalPaisa)).ToList(),
            o.Payments.Select(p => new BillPaymentDto(p.Method, p.AmountPaisa)).ToList());
    }

    private async Task<Dictionary<Guid, Customer>> CustomersAsync(IEnumerable<Guid?> ids, CancellationToken ct)
    {
        var wanted = ids.Where(i => i is not null).Select(i => i!.Value).Distinct().ToList();
        return await db.Customers.AsNoTracking().Where(c => wanted.Contains(c.Id)).ToDictionaryAsync(c => c.Id, ct);
    }

    private static BillSummaryDto Summary(SyncedOrder o, Dictionary<Guid, Customer> customers, bool fullPhone)
    {
        var customer = o.CustomerId is { } cid ? customers.GetValueOrDefault(cid) : null;
        return new BillSummaryDto(o.Id, o.ReceiptNumber, o.CompletedAt, o.OrderType, o.EmployeeName, customer?.Id, customer?.Name,
            customer is null ? null : fullPhone ? customer.Phone : PhoneNumbers.Mask(customer.Phone),
            o.Lines.Sum(l => l.Quantity), o.TotalPaisa, o.Payments.Select(p => p.Method).Distinct().ToList());
    }
}
