using System.Security.Claims;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Identity;
using AliceIdentityService.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Services;

// Recheck persisted account state and claims on every authenticated request. This
// makes revocation effective without changing AIS2's schema or relying on stale claims.
public sealed class AisCookieEvents(SignInManager<User> signIn, ILogger<AisCookieEvents> logger) : CookieAuthenticationEvents
{
    public override Task SigningIn(CookieSigningInContext context)
    {
        context.Properties.ExpiresUtc = context.Properties.IssuedUtc!.Value.Add(
            context.Properties.IsPersistent ? TimeSpan.FromDays(14) : TimeSpan.FromHours(8));
        context.Properties.AllowRefresh = false;
        return Task.CompletedTask;
    }

    public override async Task ValidatePrincipal(CookieValidatePrincipalContext context)
    {
        var user = await signIn.ValidateSecurityStampAsync(context.Principal);
        if (user is null || user.TwoFactorEnabled || !await signIn.CanSignInAsync(user) || await signIn.UserManager.IsLockedOutAsync(user))
        {
            var rejected = context.Principal;
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(IdentityConstants.ApplicationScheme);
            logger.LogInformation("Invalidated the session for {Email} ({UserId}) after account revalidation",
                OperationLog.Text(rejected?.FindFirstValue(Claims.Email)), rejected?.FindFirstValue(Claims.Subject));
            return;
        }

        context.ReplacePrincipal(await signIn.CreateUserPrincipalAsync(user));
        // Refresh claims in memory only. Requests must not extend the fixed expiry.
        context.ShouldRenew = false;
    }

    public override Task SigningOut(CookieSigningOutContext context)
    {
        var principal = context.HttpContext.User;
        if (principal.Identity?.IsAuthenticated == true)
        {
            var request = context.HttpContext.GetOpenIddictServerRequest();
            logger.LogInformation("Account {Email} ({UserId}) logged out (client {ClientId})",
                OperationLog.Text(principal.FindFirstValue(Claims.Email)), principal.FindFirstValue(Claims.Subject),
                request is null ? "AIS3" : OperationLog.Text(request.ClientId));
        }
        return Task.CompletedTask;
    }

    public override Task RedirectToLogin(RedirectContext<CookieAuthenticationOptions> context) =>
        RedirectOrStatus(context, StatusCodes.Status401Unauthorized);

    public override Task RedirectToAccessDenied(RedirectContext<CookieAuthenticationOptions> context) =>
        RedirectOrStatus(context, StatusCodes.Status403Forbidden);

    private static Task RedirectOrStatus(RedirectContext<CookieAuthenticationOptions> context, int status)
    {
        if (context.Request.Path.StartsWithSegments("/api")) context.Response.StatusCode = status;
        else context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}
