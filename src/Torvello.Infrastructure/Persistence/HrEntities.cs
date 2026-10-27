namespace Torvello.Infrastructure.Persistence;

/// <summary>An HR policy document. Version increases when the title or text changes, so staff re-confirm it.</summary>
public sealed class Policy
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Title { get; set; } = "";
    public string Category { get; set; } = "";
    public string Body { get; set; } = "";
    public bool Published { get; set; }
    public int Version { get; set; } = 1;
    public DateTimeOffset UpdatedAt { get; set; }
    public Guid UpdatedBy { get; set; }
    public bool Deleted { get; set; }
}

/// <summary>A staff member confirming they read a policy; the latest confirmation per person is kept.</summary>
public sealed class PolicyAcknowledgement
{
    public Guid PolicyId { get; set; }
    public Guid UserId { get; set; }
    public int Version { get; set; }
    public DateTimeOffset AcknowledgedAt { get; set; }
}

/// <summary>One clock-in to clock-out period, recorded at a till (ids come from the till).</summary>
public sealed class AttendanceRecord
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string EmployeeEmail { get; set; } = "";
    public string EmployeeName { get; set; } = "";
    public DateOnly BusinessDate { get; set; }
    public DateTimeOffset ClockInAt { get; set; }
    public DateTimeOffset? ClockOutAt { get; set; }
    public long ClockInMs { get; set; }
    public string? ClosedBy { get; set; }
}
