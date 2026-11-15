using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AliceIdentityService.Controllers;

[ApiController, Route("api/admin/users"), Authorize(Policy = AisConstants.AdminPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminUsersController(AdminUserService users) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminUserPage>> Search([FromQuery] AdminUserQuery query, CancellationToken cancellationToken)
    {
        if (AdminUserService.SearchTerms(query.Q).Length > 8)
        {
            ModelState.AddModelError(nameof(query.Q), "Use at most eight search terms.");
            return ValidationProblem(ModelState);
        }
        return await users.SearchAsync(query, cancellationToken);
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<AdminUserDetail>> Detail(string id, CancellationToken cancellationToken)
    {
        var user = await users.FindAsync(id, cancellationToken);
        return user is null ? NotFound() : user;
    }
}
