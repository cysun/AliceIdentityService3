using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Antiforgery;
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

    [HttpGet("antiforgery")]
    public IActionResult Antiforgery([FromServices] IAntiforgery antiforgery) =>
        Ok(new { UserId = users.GetUserId(User), RequestToken = antiforgery.GetAndStoreTokens(HttpContext).RequestToken });

    [HttpGet("overview")]
    public Task<AdminOverview> Overview(CancellationToken cancellationToken) => overview.ReadAsync(cancellationToken);

    [HttpGet("user-counts")]
    public Task<AdminUserCounts> UserCounts(CancellationToken cancellationToken) =>
        overview.ReadUserCountsAsync(clock.GetUtcNow(), cancellationToken);
}
