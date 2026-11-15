using AliceIdentityService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;

namespace AliceIdentityService.Controllers;

[Route("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountController(SignInManager<User> signIn) : Controller
{
    // This allowlist is exclusively for Step 1 visual review, not OIDC request validation.
    // Step 4 replaces it with context from a validated OpenIddict authorization request.
    private static readonly IReadOnlyDictionary<string, string> PreviewApplications =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["campus-portal"] = "Campus Portal",
            ["course-manager"] = "Course Manager",
            ["team-workspace"] = "Team Workspace"
        };

    [HttpGet("Login")]
    public IActionResult Login(string? preview, string? app, string? returnUrl)
    {
        if (preview == "oidc" && app is not null && PreviewApplications.ContainsKey(app))
            return AccountPage("Login", preview, app);
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(SafeReturnUrl(returnUrl));
        return View("Account", new AccountPageModel("Login") { Login = new() { ReturnUrl = SafeReturnUrl(returnUrl) } });
    }

    [HttpPost("Login"), RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> Login(LoginInputModel input)
    {
        input.ReturnUrl = SafeReturnUrl(input.ReturnUrl);
        // Preview query strings never become protocol context or a real sign-in flow.
        if (Request.Query.ContainsKey("preview") || Request.Query.ContainsKey("app")) return BadRequest();
        if (ModelState.IsValid)
        {
            var user = await signIn.UserManager.FindByNameAsync(input.Email.Trim());
            // AIS3 has no two-factor challenge yet. Never downgrade an imported
            // account with two-factor enabled to password-only authentication.
            if (user is not null && !user.TwoFactorEnabled)
            {
                var result = await signIn.PasswordSignInAsync(user, input.Password, input.RememberMe, lockoutOnFailure: true);
                if (result.Succeeded) return LocalRedirect(input.ReturnUrl);
            }
            ModelState.AddModelError(string.Empty, "Unable to sign in. Check your credentials and that your email is confirmed, or try again later.");
        }
        input.Password = string.Empty;
        ModelState.SetModelValue(nameof(input.Password), string.Empty, string.Empty);
        ModelState.Remove(nameof(input.ReturnUrl));
        return View("Account", new AccountPageModel("Login") { Login = input });
    }

    [Authorize, HttpGet("")]
    public IActionResult Home() => View("Account", new AccountPageModel("Home"));

    // GET only displays a confirmation. Signing out always requires an antiforgery-protected POST.
    [Authorize, HttpGet("Logout")]
    public IActionResult Logout(string? returnUrl) => View("Account", new AccountPageModel("Logout")
    {
        ReturnUrl = SafeReturnUrl(returnUrl, "/Account/Login")
    });

    [Authorize, HttpPost("Logout")]
    public async Task<IActionResult> SignOutAccount(string? returnUrl)
    {
        await signIn.SignOutAsync();
        return LocalRedirect(SafeReturnUrl(returnUrl, "/Account/Login"));
    }

    [HttpGet("AccessDenied")]
    public IActionResult AccessDenied()
    {
        Response.StatusCode = StatusCodes.Status403Forbidden;
        return View("Account", new AccountPageModel("AccessDenied"));
    }

    private string SafeReturnUrl(string? returnUrl, string fallback = "/Account") =>
        Url.IsLocalUrl(returnUrl) ? returnUrl! : fallback;

    [HttpGet("Register")]
    public IActionResult Register(string? preview, string? app) => AccountPage("Register", preview, app);

    [HttpGet("ForgotPassword")]
    public IActionResult ForgotPassword(string? preview, string? app) => AccountPage("ForgotPassword", preview, app);

    private IActionResult AccountPage(string page, string? preview, string? app)
    {
        var knownApp = app is not null && PreviewApplications.ContainsKey(app);
        var oidc = preview == "oidc" && knownApp;
        return View("Account", new AccountPageModel(page, oidc ? PreviewApplications[app!] : null, oidc ? app : null));
    }
}
