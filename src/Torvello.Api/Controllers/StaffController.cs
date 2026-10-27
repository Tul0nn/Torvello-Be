using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Torvello.Api.Infrastructure;
using Torvello.Application.Auth;
using Torvello.Application.Staff;
using Torvello.Domain;

namespace Torvello.Api.Controllers;

[ApiController]
[Route("api/staff")]
[Authorize(Roles = Roles.Owner)]
public sealed class StaffController(IStaffService staff) : ControllerBase
{
    [HttpGet]
    public Task<IReadOnlyList<UserDto>> List(CancellationToken ct) => staff.ListAsync(ct);

    /// <summary>Creates the account and emails an invitation link where they choose their password.</summary>
    [HttpPost]
    public async Task<ActionResult<UserDto>> Invite(InviteStaffRequest request, CancellationToken ct)
    {
        var created = await staff.InviteAsync(User.UserId(), request, HttpContext.ToRequestContext(), ct);
        return StatusCode(StatusCodes.Status201Created, created);
    }

    [HttpPut("{id:guid}")]
    public Task<UserDto> Update(Guid id, UpdateStaffRequest request, CancellationToken ct) =>
        staff.UpdateAsync(User.UserId(), id, request, HttpContext.ToRequestContext(), ct);

    [HttpPost("{id:guid}/resend-invite")]
    public async Task<IActionResult> ResendInvite(Guid id, CancellationToken ct)
    {
        await staff.ResendInviteAsync(User.UserId(), id, HttpContext.ToRequestContext(), ct);
        return Accepted();
    }
}
