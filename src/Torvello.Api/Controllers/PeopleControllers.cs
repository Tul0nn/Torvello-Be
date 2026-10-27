using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.IdentityModel.JsonWebTokens;
using Torvello.Api.Infrastructure;
using Torvello.Application.Common;
using Torvello.Application.People;
using Torvello.Domain;
using Torvello.Infrastructure.Persistence;

namespace Torvello.Api.Controllers;

internal static class EditorName
{
    /// <summary>The signed-in person's display name, recorded as "updated by" on website edits.</summary>
    public static async Task<string> OfAsync(ClaimsPrincipal user, UserManager<AppUser> users) =>
        (await users.FindByIdAsync(user.UserId().ToString()))?.DisplayName ?? throw AppException.Unauthorized("Please sign in again");
}

/// <summary>Customers recorded at the till. Managers see masked phone numbers; owners see them in full and may edit.</summary>
[ApiController]
[Route("api/customers")]
[Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
public sealed class CustomersController(ICustomerService customers, UserManager<AppUser> users) : ControllerBase
{
    private bool FullPhone => User.IsInRole(Roles.Owner);

    [HttpGet]
    public Task<CustomerPageDto> List([FromQuery] string? search, [FromQuery] string? sort, [FromQuery] int page = 1, [FromQuery] int pageSize = 25,
        CancellationToken ct = default) =>
        customers.ListAsync(new CustomerQuery(search, sort, page, pageSize), FullPhone, ct);

    [HttpGet("{id:guid}")]
    public Task<CustomerDetailDto> Get(Guid id, CancellationToken ct) => customers.GetAsync(id, FullPhone, ct);

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Owner)]
    public async Task<CustomerDetailDto> Update(Guid id, CustomerUpdate update, CancellationToken ct) =>
        await customers.UpdateAsync(await EditorName.OfAsync(User, users), id, update, ct);
}

/// <summary>Staff details kept on the till. Owners see personal details and may edit; everyone can read their own.</summary>
[ApiController]
[Route("api/employees")]
[Authorize]
public sealed class EmployeesController(IEmployeeService employees, UserManager<AppUser> users) : ControllerBase
{
    private Viewer Viewer => new(User.UserId(), User.FindFirstValue(JwtRegisteredClaimNames.Email) ?? "", "", User.IsInRole(Roles.Owner));

    [HttpGet]
    [Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
    public Task<IReadOnlyList<EmployeeSummaryDto>> List(CancellationToken ct) => employees.ListAsync(ct);

    [HttpGet("me")]
    public Task<EmployeeProfileDto> Me(CancellationToken ct) => employees.MeAsync(Viewer, ct);

    [HttpGet("{id:guid}")]
    [Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
    public Task<EmployeeProfileDto> Get(Guid id, CancellationToken ct) => employees.GetAsync(Viewer, id, ct);

    [HttpPut("{id:guid}")]
    [Authorize(Roles = Roles.Owner)]
    public async Task<EmployeeProfileDto> Update(Guid id, EmployeeProfileUpdate update, CancellationToken ct) =>
        await employees.UpdateAsync(await EditorName.OfAsync(User, users), id, update, ct);
}

/// <summary>Bills uploaded by the tills.</summary>
[ApiController]
[Route("api/orders")]
[Authorize(Roles = $"{Roles.Owner},{Roles.Manager}")]
public sealed class OrdersController(IBillService bills) : ControllerBase
{
    private bool FullPhone => User.IsInRole(Roles.Owner);

    [HttpGet]
    public Task<IReadOnlyList<BillSummaryDto>> List([FromQuery] DateOnly? date, CancellationToken ct) => bills.ListAsync(date, FullPhone, ct);

    [HttpGet("{id:guid}")]
    public Task<BillDto> Get(Guid id, CancellationToken ct) => bills.GetAsync(id, FullPhone, ct);
}
