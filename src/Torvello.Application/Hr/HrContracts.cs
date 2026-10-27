namespace Torvello.Application.Hr;

public sealed record PolicyInput(string Title, string Category, string Body, bool Published);

public sealed record PolicyDto(
    Guid Id,
    string Title,
    string Category,
    string Body,
    bool Published,
    int Version,
    DateTimeOffset UpdatedAt,
    string UpdatedByName,
    int? AcknowledgedVersion,
    DateTimeOffset? AcknowledgedAt);

public sealed record PolicyAckDto(
    Guid UserId,
    string DisplayName,
    string Email,
    string Role,
    int? AcknowledgedVersion,
    DateTimeOffset? AcknowledgedAt);

public sealed record AttendanceRecordDto(
    Guid Id,
    string EmployeeName,
    string EmployeeEmail,
    DateOnly BusinessDate,
    DateTimeOffset ClockInAt,
    DateTimeOffset? ClockOutAt,
    int? Minutes);

public sealed record AttendanceTotalDto(string EmployeeEmail, string EmployeeName, int Days, int Minutes, bool Open);

public sealed record AttendanceReportDto(
    DateOnly From,
    DateOnly To,
    IReadOnlyList<AttendanceRecordDto> Records,
    IReadOnlyList<AttendanceTotalDto> Totals);

public interface IPolicyService
{
    /// <summary>Managers see drafts; other staff only published policies.</summary>
    Task<IReadOnlyList<PolicyDto>> ListAsync(Guid userId, bool canManage, CancellationToken ct);
    Task<PolicyDto> GetAsync(Guid userId, bool canManage, Guid policyId, CancellationToken ct);
    Task<PolicyDto> CreateAsync(Guid actorId, PolicyInput input, CancellationToken ct);
    Task<PolicyDto> UpdateAsync(Guid actorId, Guid policyId, PolicyInput input, CancellationToken ct);
    Task DeleteAsync(Guid actorId, Guid policyId, CancellationToken ct);
    Task AcknowledgeAsync(Guid userId, Guid policyId, CancellationToken ct);
    Task<IReadOnlyList<PolicyAckDto>> AcknowledgementsAsync(Guid policyId, CancellationToken ct);
}

public interface IAttendanceService
{
    Task<AttendanceReportDto> ReportAsync(DateOnly? from, DateOnly? to, CancellationToken ct);
}
