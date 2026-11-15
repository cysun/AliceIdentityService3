using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;

namespace AliceIdentityService.Controllers;

[ApiController, Route("api/admin"), Authorize(Policy = AisConstants.AdminPolicy)]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AdminController(UserManager<User> users, AdminOverviewService overview, TimeProvider clock) : ControllerBase
{
    [HttpGet("session")]
    public async Task<ActionResult<AdminSession>> Session()
    {
        var ticket = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var user = await users.GetUserAsync(User);
        if (user is null || ticket.Properties?.ExpiresUtc is not { } expiry) return Unauthorized();
        return new AdminSession(new(user.Id, user.Email, user.FirstName, user.LastName, user.ScreenName),
            expiry, clock.GetUtcNow(), ApplicationInfo.Current);
    }

    [HttpGet("overview")]
    public Task<AdminOverview> Overview(CancellationToken cancellationToken) => overview.ReadAsync(cancellationToken);
}
