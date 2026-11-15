using System.Globalization;
using System.Security.Claims;
using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using OpenIddict.Abstractions;
using OpenIddict.Server.AspNetCore;
using Microsoft.Extensions.Primitives;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Controllers;

[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AuthorizationController(SignInManager<User> signIn, IOpenIddictApplicationManager applications,
    IOpenIddictAuthorizationManager authorizations, OidcLoginContext loginContext, OidcPrincipalService principals,
    IAntiforgery antiforgery) : Controller
{
    private const string Scheme = OpenIddictServerAspNetCoreDefaults.AuthenticationScheme;

    [HttpGet("~/connect/authorize"), HttpPost("~/connect/authorize"), IgnoreAntiforgeryToken]
    public async Task<IActionResult> AuthorizeClient()
    {
        // OpenIddict validates client, redirect, scopes, response type and PKCE before passthrough.
        var request = HttpContext.GetOpenIddictServerRequest()!;
        var application = (await applications.FindByClientIdAsync(request.ClientId!))!;
        if (!await applications.HasConsentTypeAsync(application, ConsentTypes.Implicit) || request.HasPromptValue(PromptValues.Consent))
            return ProtocolError(Errors.ConsentRequired, "This client requires a consent flow that is not available.");

        var session = await HttpContext.AuthenticateAsync(IdentityConstants.ApplicationScheme);
        var issued = session.Properties?.IssuedUtc;
        var challenge = !session.Succeeded || issued is null || request.HasPromptValue(PromptValues.Login) ||
            request.HasPromptValue(PromptValues.SelectAccount) || request.MaxAge is 0 ||
            (request.MaxAge is not null && (DateTimeOffset.UtcNow - issued!.Value).TotalSeconds > request.MaxAge.Value);
        if (challenge)
        {
            if (request.HasPromptValue(PromptValues.None)) return ProtocolError(Errors.LoginRequired, "Sign-in is required.");
            IEnumerable<KeyValuePair<string, StringValues>> values = Request.HasFormContentType ? await Request.ReadFormAsync() : Request.Query;
            var continuation = Request.PathBase + Request.Path + QueryString.Create(values);
            var context = loginContext.Create(request.ClientId!, continuation);
            return View("~/Views/Account/Account.cshtml", new AccountPageModel("Login",
                await applications.GetDisplayNameAsync(application) ?? request.ClientId, context)
            {
                Login = new() { OidcContext = context }
            });
        }

        var user = await signIn.UserManager.GetUserAsync(session.Principal!);
        if (user is null || !await principals.CanSignInAsync(user)) return ProtocolError(Errors.LoginRequired, "Sign-in is required.");
        var principal = await principals.CreateAsync(user, request.GetScopes(), issued!.Value.ToUnixTimeSeconds());
        object? authorization = null;
        await foreach (var item in authorizations.FindAsync(subject: user.Id, client: await applications.GetIdAsync(application),
            status: Statuses.Valid, type: AuthorizationTypes.Permanent, scopes: principal.GetScopes())) authorization = item;
        authorization ??= await authorizations.CreateAsync(principal, user.Id, (await applications.GetIdAsync(application))!,
            AuthorizationTypes.Permanent, principal.GetScopes());
        principal.SetAuthorizationId(await authorizations.GetIdAsync(authorization));
        return SignIn(principal, Scheme);
    }

    [HttpPost("~/connect/token"), IgnoreAntiforgeryToken, Produces("application/json")]
    public async Task<IActionResult> Exchange()
    {
        var request = HttpContext.GetOpenIddictServerRequest()!;
        var ticket = await HttpContext.AuthenticateAsync(Scheme);
        if (!ticket.Succeeded || ticket.Principal is null) return ProtocolError(Errors.InvalidGrant, "The grant is no longer valid.");
        var previous = ticket.Principal;
        var user = await signIn.ValidateSecurityStampAsync(previous);
        if (user is null || !await principals.CanSignInAsync(user)) return ProtocolError(Errors.InvalidGrant, "The grant is no longer valid.");
        var application = (await applications.FindByClientIdAsync(request.ClientId!))!;
        var grantedScopes = previous.GetScopes();
        if (request.IsRefreshTokenGrantType() && !string.IsNullOrEmpty(request.Scope))
            grantedScopes = request.GetScopes();
        foreach (var scope in grantedScopes)
            if (scope is not Scopes.OpenId and not Scopes.OfflineAccess &&
                !await applications.HasPermissionAsync(application, Permissions.Prefixes.Scope + scope))
                return ProtocolError(Errors.InvalidGrant, "The client is no longer allowed to use this grant.");

        if (!long.TryParse(previous.GetClaim(Claims.AuthenticationTime), CultureInfo.InvariantCulture, out var authenticationTime))
            return ProtocolError(Errors.InvalidGrant, "The grant is no longer valid.");
        var principal = await principals.CreateAsync(user, grantedScopes, authenticationTime);
        principal.SetAuthorizationId(previous.GetAuthorizationId());
        principal.SetPresenters(previous.GetPresenters());
        principal.SetClaim(Claims.Private.Nonce, previous.GetClaim(Claims.Private.Nonce));
        return SignIn(principal, Scheme);
    }

    [HttpGet("~/connect/userinfo"), HttpPost("~/connect/userinfo"), IgnoreAntiforgeryToken, Produces("application/json")]
    public async Task<IActionResult> UserInfo()
    {
        var ticket = await HttpContext.AuthenticateAsync(Scheme);
        if (!ticket.Succeeded || ticket.Principal is null) return ProtocolError(Errors.InvalidToken, "The token is no longer valid.");
        var user = await signIn.UserManager.GetUserAsync(ticket.Principal);
        if (user is null || !await principals.CanSignInAsync(user)) return ProtocolError(Errors.InvalidToken, "The token is no longer valid.");
        var principal = await principals.CreateAsync(user, ticket.Principal.GetScopes(), 0);
        var claims = new Dictionary<string, object> { [Claims.Subject] = user.Id };
        foreach (var group in principal.Claims.Where(claim => claim.Type != Claims.AuthenticationTime &&
            claim.GetDestinations().Contains(Destinations.IdentityToken)).GroupBy(claim => claim.Type))
        {
            var values = group.Select(claim => claim.ValueType == ClaimValueTypes.Boolean ? (object)bool.Parse(claim.Value) : claim.Value).ToArray();
            claims[group.Key] = values.Length == 1 ? values[0] : values;
        }
        return Ok(claims);
    }

    [HttpGet("~/connect/logout"), HttpPost("~/connect/logout"), IgnoreAntiforgeryToken]
    public async Task<IActionResult> EndSession()
    {
        var request = HttpContext.GetOpenIddictServerRequest()!;
        if (!HttpMethods.IsPost(Request.Method) || Request.Form["confirm"] != "yes")
        {
            var application = request.ClientId is not null ? await applications.FindByClientIdAsync(request.ClientId) : null;
            IEnumerable<KeyValuePair<string, StringValues>> parameters = Request.HasFormContentType ? await Request.ReadFormAsync() : Request.Query;
            return View("~/Views/Account/Account.cshtml", new AccountPageModel("OidcLogout",
                application is null ? null : await applications.GetDisplayNameAsync(application))
            {
                LogoutParameters = parameters.Where(pair => pair.Key is not "confirm" and not "__RequestVerificationToken")
                    .SelectMany(pair => pair.Value.Select(value => new KeyValuePair<string, string>(pair.Key, value ?? ""))).ToArray()
            });
        }
        try { await antiforgery.ValidateRequestAsync(HttpContext); }
        catch (AntiforgeryValidationException) { return BadRequest(); }
        await signIn.SignOutAsync();
        return SignOut(new AuthenticationProperties { RedirectUri = "/Account/Login" }, Scheme);
    }

    private ForbidResult ProtocolError(string error, string description) => Forbid(new AuthenticationProperties(new Dictionary<string, string?>
    {
        [OpenIddictServerAspNetCoreConstants.Properties.Error] = error,
        [OpenIddictServerAspNetCoreConstants.Properties.ErrorDescription] = description
    }), Scheme);
}
