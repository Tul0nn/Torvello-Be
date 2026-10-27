namespace Torvello.Infrastructure.Persistence;

/// <summary>A cafe customer, identified by mobile number (+8801XXXXXXXXX). Details are edited at the till.</summary>
public sealed class Customer
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Phone { get; set; } = "";
    public string? Name { get; set; }
    public int? BirthdayMonth { get; set; }
    public int? BirthdayDay { get; set; }
    public int? AnniversaryMonth { get; set; }
    public int? AnniversaryDay { get; set; }
    public string? Notes { get; set; }
    public bool MarketingConsent { get; set; }
    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>When and by whom the details were last edited (till or website); the latest edit wins.</summary>
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedByName { get; set; }

    /// <summary>The till's own id for this customer, so website edits can be applied there.</summary>
    public Guid? TillCustomerId { get; set; }
}

/// <summary>
/// Changes made on the website that tills must download (customer and staff details edited by the owner).
/// Sequence is the cursor tills keep; payloads with a national ID are stored encrypted.
/// </summary>
public sealed class ServerChange
{
    public long Sequence { get; set; }
    public string EntityType { get; set; } = "";
    public Guid EntityId { get; set; }
    public string ProtectedPayload { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// A staff member as kept on the till (ids come from the till). Managers edit these details at the till;
/// the owner reads them on the website. The national ID is stored encrypted.
/// </summary>
public sealed class StaffRecord
{
    public Guid Id { get; set; }
    public Guid DeviceId { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Role { get; set; } = "";
    public bool Active { get; set; } = true;
    public string? Position { get; set; }
    public string? Phone { get; set; }
    public DateOnly? JoinDate { get; set; }
    public DateOnly? DateOfBirth { get; set; }
    public string? Address { get; set; }
    public string? NationalIdProtected { get; set; }
    public string? EmergencyContactName { get; set; }
    public string? EmergencyContactPhone { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public string? UpdatedByName { get; set; }
}
