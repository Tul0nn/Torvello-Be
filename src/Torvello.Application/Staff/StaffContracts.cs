using Torvello.Application.Auth;

namespace Torvello.Application.Staff;

public sealed record InviteStaffRequest(string Email, string DisplayName, string Role);

public sealed record UpdateStaffRequest(string DisplayName, string Role, bool IsActive);

public interface IStaffService
{
    Task<IReadOnlyList<UserDto>> ListAsync(CancellationToken ct);
    Task<UserDto> InviteAsync(Guid actorId, InviteStaffRequest request, RequestContext context, CancellationToken ct);
    Task<UserDto> UpdateAsync(Guid actorId, Guid staffId, UpdateStaffRequest request, RequestContext context, CancellationToken ct);
    Task ResendInviteAsync(Guid actorId, Guid staffId, RequestContext context, CancellationToken ct);
}
