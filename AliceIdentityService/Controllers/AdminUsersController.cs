using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AliceIdentityService.Controllers;

[ApiController, Route("api/admin/users"), Authorize(Policy = AisConstants.AdminPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
[RequestSizeLimit(48 * 1024)]
public sealed class AdminUsersController(AdminUserService users, AdminUserMutationService mutations) : ControllerBase
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

    [HttpPost]
    public async Task<IActionResult> Create(AdminCreateUserInput input, CancellationToken cancellationToken) =>
        await MutationResult(await mutations.CreateAsync(User, input, cancellationToken));

    [HttpPost("{id}/profile")]
    public async Task<IActionResult> Edit(string id, AdminEditUserInput input, CancellationToken cancellationToken) =>
        await MutationResult(await mutations.EditAsync(User, id, input, cancellationToken));

    [HttpPost("{id}/password")]
    public async Task<IActionResult> Password(string id, AdminPasswordInput input, CancellationToken cancellationToken) =>
        await MutationResult(await mutations.PasswordAsync(User, id, input, cancellationToken));

    [HttpPost("{id}/lock")]
    public async Task<IActionResult> Lock(string id, AdminLockInput input, CancellationToken cancellationToken) =>
        await MutationResult(await mutations.LockAsync(User, id, input, cancellationToken));

    [HttpPost("{id}/unlock")]
    public async Task<IActionResult> Unlock(string id, AdminUserVersionInput input, CancellationToken cancellationToken) =>
        await MutationResult(await mutations.UnlockAsync(User, id, input, cancellationToken));

    [HttpDelete("{id}")]
    public async Task<IActionResult> Delete(string id, AdminDeleteUserInput input, CancellationToken cancellationToken) =>
        await MutationResult(await mutations.DeleteAsync(User, id, input, cancellationToken));

    private async Task<IActionResult> MutationResult(AdminMutationOutcome outcome)
    {
        if (outcome.Status == 401) return Unauthorized();
        if (outcome.Status == 403) return Forbid();
        if (outcome.Status != 200) return Problem(statusCode: outcome.Status, title: outcome.Error);
        if (outcome.SessionEnded) await HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
        return Ok(new AdminMutationResult(outcome.Id!, outcome.SessionEnded));
    }
}
