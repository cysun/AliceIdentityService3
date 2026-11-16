using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace AliceIdentityService.Controllers;

[ApiController, Route("api/admin/scopes"), Authorize(Policy = AisConstants.AdminPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(48 * 1024)]
public sealed class AdminScopesController(AdminScopeService scopes) : ControllerBase
{
    [HttpGet]
    public async Task<ActionResult<AdminScopePage>> Search([FromQuery] AdminScopeQuery query, CancellationToken cancellationToken) =>
        await scopes.SearchAsync(query, cancellationToken);

    [HttpGet("{id}")]
    public async Task<ActionResult<AdminScopeDetail>> Detail(string id, CancellationToken cancellationToken)
    {
        var scope = await scopes.FindAsync(id, cancellationToken);
        return scope is null ? NotFound() : scope;
    }

    [HttpPost]
    public async Task<IActionResult> Create(AdminScopeInput input, CancellationToken cancellationToken) =>
        Result(await scopes.CreateAsync(User, input, cancellationToken));

    [HttpPost("{id}")]
    public async Task<IActionResult> Edit(string id, AdminEditScopeInput input, CancellationToken cancellationToken) =>
        Result(await scopes.EditAsync(User, id, input, cancellationToken));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, AdminDeleteUserInput input, CancellationToken cancellationToken) =>
        Result(await scopes.DeleteAsync(User, id, input, cancellationToken));

    private IActionResult Result(AdminMutationOutcome outcome) => outcome.Status switch
    {
        200 => Ok(new AdminMutationResult(outcome.Id!, false)),
        401 => Unauthorized(),
        403 => Forbid(),
        _ => Problem(statusCode: outcome.Status, title: outcome.Error)
    };
}
