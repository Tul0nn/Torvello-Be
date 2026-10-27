using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Torvello.Api.Infrastructure;
using Torvello.Application.Hr;
using Torvello.Domain;

namespace Torvello.Api.Controllers;

/// <summary>HR policy documents. Every staff member reads and confirms them; owners and managers write them.</summary>
[ApiController]
[Route("api/hr/policies")]
[Authorize]
public sealed class PoliciesController(IPolicyService policies) : ControllerBase
{
    private bool CanManage => User.IsInRole(Roles.Owner) || User.IsInRole(Roles.Manager);

    [HttpGet]
    public Task<IReadOnlyList<PolicyDto>> List(CancellationToken ct) => policies.ListAsync(User.UserId(), CanManage, ct);

    [HttpGet("{id:guid}")]
    public Task<PolicyDto> Get(Guid id, CancellationToken ct) => policies.GetAsync(User.UserId(), CanManage, id, ct);

    [HttpPost]
    [Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
    public async Task<ActionResult<PolicyDto>> Create(PolicyInput input, CancellationToken ct) =>
        StatusCode(StatusCodes.Status201Created, await policies.CreateAsync(User.UserId(), input, ct));

    [HttpPut("{id:guid}")]
    [Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
    public Task<PolicyDto> Update(Guid id, PolicyInput input, CancellationToken ct) => policies.UpdateAsync(User.UserId(), id, input, ct);

    [HttpDelete("{id:guid}")]
    [Authorize(Roles = Roles.Owner)]
    public async Task<IActionResult> Delete(Guid id, CancellationToken ct)
    {
        await policies.DeleteAsync(User.UserId(), id, ct);
        return NoContent();
    }

    [HttpPost("{id:guid}/acknowledge")]
    public async Task<IActionResult> Acknowledge(Guid id, CancellationToken ct)
    {
        await policies.AcknowledgeAsync(User.UserId(), id, ct);
        return NoContent();
    }

    [HttpGet("{id:guid}/acknowledgements")]
    [Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
    public Task<IReadOnlyList<PolicyAckDto>> Acknowledgements(Guid id, CancellationToken ct) => policies.AcknowledgementsAsync(id, ct);
}

/// <summary>Clock-in / clock-out periods recorded at the tills.</summary>
[ApiController]
[Route("api/hr/attendance")]
[Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
public sealed class AttendanceController(IAttendanceService attendance) : ControllerBase
{
    [HttpGet]
    public Task<AttendanceReportDto> Report([FromQuery] DateOnly? from, [FromQuery] DateOnly? to, CancellationToken ct) =>
        attendance.ReportAsync(from, to, ct);
}
