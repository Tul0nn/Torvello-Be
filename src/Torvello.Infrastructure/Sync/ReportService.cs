using Microsoft.EntityFrameworkCore;
using Torvello.Application.Sync;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Sync;

public sealed class ReportService(AppDbContext db, TimeProvider clock) : IReportService
{
    private static readonly string[] Methods = ["cash", "card", "bkash", "nagad"];

    public async Task<DailyReportDto> DailyAsync(DateOnly? date, CancellationToken ct)
    {
        var day = date ?? BusinessTime.Today(clock);
        // One day of one cafe is small enough to total in memory.
        var orders = await db.SyncedOrders.AsNoTracking()
            .Where(o => o.BusinessDate == day)
            .Include(o => o.Lines)
            .Include(o => o.Payments)
            .ToListAsync(ct);

        var byMethod = Methods.ToDictionary(m => m, m => orders.SelectMany(o => o.Payments).Where(p => p.Method == m).Sum(p => p.AmountPaisa));
        var topItems = orders.SelectMany(o => o.Lines)
            .GroupBy(l => l.Name)
            .Select(g => new TopItemDto(g.Key, g.Sum(l => l.Quantity), g.Sum(l => l.LineTotalPaisa)))
            .OrderByDescending(i => i.Quantity).ThenByDescending(i => i.SalesPaisa)
            .Take(10)
            .ToList();
        var lastSeen = (await db.Devices.AsNoTracking().Select(d => d.LastSeenAt).ToListAsync(ct)).Max();

        return new DailyReportDto(day, orders.Count, orders.Sum(o => o.TotalPaisa), orders.Sum(o => o.DiscountPaisa), byMethod, topItems, lastSeen);
    }
}
