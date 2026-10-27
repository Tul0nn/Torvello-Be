namespace Torvello.Application.People;

// Customers, staff details and bills are entered by managers at the till and reach the backend
// through sync. The owner may also edit customer and staff details on the website; those edits
// go into the change feed that tills download. The newest edit wins, and records show who made it.

public sealed record CustomerSummaryDto(
    Guid Id,
    string Phone,
    string? Name,
    int Visits,
    long TotalSpentPaisa,
    DateTimeOffset? LastVisitAt,
    int? BirthdayMonth,
    int? BirthdayDay);

public sealed record CustomerPageDto(IReadOnlyList<CustomerSummaryDto> Items, int Total, int Page, int PageSize);

public sealed record CustomerOrderDto(Guid Id, string ReceiptNumber, DateTimeOffset CompletedAt, long TotalPaisa, int ItemCount);

public sealed record CustomerDetailDto(
    Guid Id,
    string Phone,
    string? Name,
    int Visits,
    long TotalSpentPaisa,
    DateTimeOffset? LastVisitAt,
    int? BirthdayMonth,
    int? BirthdayDay,
    int? AnniversaryMonth,
    int? AnniversaryDay,
    string? Notes,
    bool MarketingConsent,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? UpdatedByName,
    IReadOnlyList<CustomerOrderDto> RecentOrders);

/// <summary>Owner edits on the website; they are downloaded by the till.</summary>
public sealed record CustomerUpdate(
    string? Name,
    int? BirthdayMonth,
    int? BirthdayDay,
    int? AnniversaryMonth,
    int? AnniversaryDay,
    string? Notes,
    bool MarketingConsent);

/// <summary>Owner edits of staff details on the website; they are downloaded by the till.</summary>
public sealed record EmployeeProfileUpdate(
    string? Position,
    string? Phone,
    DateOnly? JoinDate,
    DateOnly? DateOfBirth,
    string? Address,
    string? NationalId,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? Notes);

public sealed record CustomerQuery(string? Search, string? Sort, int Page = 1, int PageSize = 25);

public sealed record EmployeeSummaryDto(
    Guid Id,
    string Name,
    string Email,
    string Role,
    bool Active,
    string? Position,
    string? Phone,
    DateOnly? JoinDate,
    bool HasWebsiteAccount,
    bool ClockedInNow);

public sealed record AttendanceSummary(int Days, int Minutes);

public sealed record PolicySummary(int Read, int Total);

public sealed record EmployeeProfileDto(
    Guid Id,
    string Name,
    string Email,
    string Role,
    bool Active,
    string? Position,
    string? Phone,
    DateOnly? JoinDate,
    bool HasWebsiteAccount,
    bool ClockedInNow,
    DateOnly? DateOfBirth,
    string? Address,
    string? NationalId,
    string? EmergencyContactName,
    string? EmergencyContactPhone,
    string? Notes,
    bool CanSeeSensitive,
    DateTimeOffset? UpdatedAt,
    string? UpdatedByName,
    AttendanceSummary AttendanceLast30Days,
    PolicySummary? Policies);

public sealed record BillLineDto(string Name, int Quantity, long UnitPricePaisa, long LineTotalPaisa);

public sealed record BillPaymentDto(string Method, long AmountPaisa);

public sealed record BillSummaryDto(
    Guid Id,
    string ReceiptNumber,
    DateTimeOffset CompletedAt,
    string OrderType,
    string? EmployeeName,
    Guid? CustomerId,
    string? CustomerName,
    string? CustomerPhone,
    int ItemCount,
    long TotalPaisa,
    IReadOnlyList<string> Methods);

public sealed record BillDto(
    Guid Id,
    string ReceiptNumber,
    DateTimeOffset CompletedAt,
    string OrderType,
    string? EmployeeName,
    Guid? CustomerId,
    string? CustomerName,
    string? CustomerPhone,
    int ItemCount,
    long TotalPaisa,
    IReadOnlyList<string> Methods,
    long SubtotalPaisa,
    long DiscountPaisa,
    IReadOnlyList<BillLineDto> Lines,
    IReadOnlyList<BillPaymentDto> Payments);

/// <summary>Who is looking. Owners see personal details and full phone numbers; managers see work details.</summary>
public sealed record Viewer(Guid UserId, string Email, string DisplayName, bool IsOwner);

public interface ICustomerService
{
    Task<CustomerPageDto> ListAsync(CustomerQuery query, bool fullPhone, CancellationToken ct);
    Task<CustomerDetailDto> GetAsync(Guid id, bool fullPhone, CancellationToken ct);
    Task<CustomerDetailDto> UpdateAsync(string editorName, Guid id, CustomerUpdate update, CancellationToken ct);
}

public interface IEmployeeService
{
    Task<IReadOnlyList<EmployeeSummaryDto>> ListAsync(CancellationToken ct);
    Task<EmployeeProfileDto> GetAsync(Viewer viewer, Guid id, CancellationToken ct);
    /// <summary>The viewer's own staff record, found by their email.</summary>
    Task<EmployeeProfileDto> MeAsync(Viewer viewer, CancellationToken ct);
    Task<EmployeeProfileDto> UpdateAsync(string editorName, Guid id, EmployeeProfileUpdate update, CancellationToken ct);
}

public interface IBillService
{
    Task<IReadOnlyList<BillSummaryDto>> ListAsync(DateOnly? date, bool fullPhone, CancellationToken ct);
    Task<BillDto> GetAsync(Guid id, bool fullPhone, CancellationToken ct);
}
