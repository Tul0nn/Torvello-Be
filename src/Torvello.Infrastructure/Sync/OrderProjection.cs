using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Sync;

/// <summary>Why an uploaded event was refused; the message goes back to the till.</summary>
public sealed class SyncRejection(string reason) : Exception(reason);

/// <summary>
/// Turns an order_completed payload from the till into a SyncedOrder, re-checking every
/// amount: the server never trusts totals it cannot reproduce from the lines.
/// </summary>
public static partial class OrderProjection
{
    private static readonly HashSet<string> PaymentMethods = ["cash", "card", "bkash", "nagad"];
    private static readonly HashSet<string> OrderTypes = ["dine_in", "takeaway"];

    public static SyncedOrder Parse(JsonElement payload, Guid deviceId, Guid eventId, DateTimeOffset eventCreatedAt)
    {
        if (payload.ValueKind != JsonValueKind.Object) throw new SyncRejection("Payload must be an object");

        var orderId = Guid.TryParse(Str(payload, "orderId"), out var id) ? id : throw new SyncRejection("orderId is missing");
        var receipt = Str(payload, "receiptNumber");
        if (string.IsNullOrWhiteSpace(receipt) || receipt.Length > 40) throw new SyncRejection("receiptNumber is missing");

        var totals = Obj(payload, "totals");
        var subtotal = Money(totals, "subtotalPaisa");
        var discount = Money(totals, "discountPaisa");
        var total = Money(totals, "totalPaisa");

        var lines = Arr(payload, "lines").Select(line =>
        {
            var name = Str(line, "name");
            if (string.IsNullOrWhiteSpace(name) || name.Length > 120) throw new SyncRejection("A line has no name");
            var quantity = line.TryGetProperty("quantity", out var q) && q.TryGetInt32(out var qty) && qty is > 0 and <= 10_000
                ? qty
                : throw new SyncRejection("A line has an invalid quantity");
            var unit = Money(line, "unitPricePaisa");
            return new SyncedOrderLine { OrderId = orderId, Name = name, Quantity = quantity, UnitPricePaisa = unit, LineTotalPaisa = checked(unit * quantity) };
        }).ToList();
        if (lines.Count is 0 or > 200) throw new SyncRejection("An order needs 1 to 200 lines");

        var payments = Arr(payload, "payments").Select(p =>
        {
            var method = Str(p, "method");
            if (method is null || !PaymentMethods.Contains(method)) throw new SyncRejection("Unknown payment method");
            var amount = Money(p, "amountPaisa");
            if (amount == 0) throw new SyncRejection("Payment amounts must be above zero");
            return new SyncedPayment { OrderId = orderId, Method = method, AmountPaisa = amount };
        }).ToList();

        if (lines.Sum(l => l.LineTotalPaisa) != subtotal) throw new SyncRejection("Lines do not add up to the subtotal");
        if (discount > subtotal || subtotal - discount != total) throw new SyncRejection("Total does not match subtotal minus discount");
        if (payments.Sum(p => p.AmountPaisa) != total) throw new SyncRejection("Payments do not add up to the total");

        var completedAt = DateTimeOffset.TryParse(Str(payload, "completedAt"), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var c)
            ? c
            : eventCreatedAt;
        var orderType = Str(payload, "orderType");
        var employee = Str(payload, "employeeName");

        return new SyncedOrder
        {
            Id = orderId,
            DeviceId = deviceId,
            SourceEventId = eventId,
            ReceiptNumber = receipt,
            BusinessDate = BusinessDate(Str(payload, "businessDate"), receipt, completedAt),
            OrderType = orderType is not null && OrderTypes.Contains(orderType) ? orderType : "unknown",
            EmployeeName = employee is { Length: > 0 and <= 60 } ? employee : null,
            SubtotalPaisa = subtotal,
            DiscountPaisa = discount,
            TotalPaisa = total,
            CompletedAt = completedAt,
            CompletedAtMs = completedAt.ToUnixTimeMilliseconds(),
            Lines = lines,
            Payments = payments,
        };
    }

    /// <summary>Payload date, else the date inside TV-YYYYMMDD-NNNN, else the completion time in Dhaka.</summary>
    private static DateOnly BusinessDate(string? value, string receipt, DateTimeOffset completedAt)
    {
        if (DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)) return date;
        var match = ReceiptDate().Match(receipt);
        if (match.Success && DateOnly.TryParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture, DateTimeStyles.None, out date)) return date;
        return DateOnly.FromDateTime(BusinessTime.ToDhaka(completedAt).DateTime);
    }

    private static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static JsonElement Obj(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Object ? v : throw new SyncRejection($"{name} is missing");

    private static IEnumerable<JsonElement> Arr(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : throw new SyncRejection($"{name} is missing");

    private static long Money(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.TryGetInt64(out var n) && n >= 0 ? n : throw new SyncRejection($"{name} is not a valid amount");

    [GeneratedRegex(@"^TV-(\d{8})-\d+$")]
    private static partial Regex ReceiptDate();
}

public static class BusinessTime
{
    private static readonly TimeZoneInfo Dhaka = FindDhaka();

    public static DateTimeOffset ToDhaka(DateTimeOffset at) => TimeZoneInfo.ConvertTime(at, Dhaka);

    public static DateOnly Today(TimeProvider clock) => DateOnly.FromDateTime(ToDhaka(clock.GetUtcNow()).DateTime);

    private static TimeZoneInfo FindDhaka()
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById("Asia/Dhaka");
        }
        catch (TimeZoneNotFoundException)
        {
            return TimeZoneInfo.CreateCustomTimeZone("Asia/Dhaka", TimeSpan.FromHours(6), "Dhaka", "Dhaka");
        }
    }
}
