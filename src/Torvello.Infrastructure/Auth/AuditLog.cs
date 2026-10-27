using System.Text.Json;
using Torvello.Application.Auth;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Infrastructure.Auth;

/// <summary>Adds audit rows to the current unit of work; the caller saves them with its other changes.</summary>
public sealed class AuditLog(AppDbContext db, TimeProvider clock)
{
    public void Add(string action, Guid? userId, RequestContext context, object? details = null)
    {
        db.AuditEvents.Add(new AuditEvent
        {
            UserId = userId,
            Action = action,
            Details = details is null ? null : JsonSerializer.Serialize(details),
            IpAddress = context.IpAddress,
            OccurredAt = clock.GetUtcNow(),
        });
    }
}
