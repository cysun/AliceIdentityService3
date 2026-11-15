using AliceIdentityService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using AliceIdentityService.Services;

namespace AliceIdentityService.Controllers;

[Route("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountController(SignInManager<User> signIn, OidcLoginContext oidcContext) : Controller
{
    [HttpGet("Login")]
    public async Task<IActionResult> Login(string? context, string? returnUrl)
    {
        if (context is not null)
        {
            var validated = await oidcContext.ReadAsync(context);
            if (validated is null) return InvalidContext();
            return View("Account", new AccountPageModel("Login", validated.ApplicationName, context)
            {
                Login = new() { OidcContext = context }
            });
        }
        if (User.Identity?.IsAuthenticated == true) return LocalRedirect(SafeReturnUrl(returnUrl));
        return View("Account", new AccountPageModel("Login") { Login = new() { ReturnUrl = SafeReturnUrl(returnUrl) } });
    }

    [HttpPost("Login"), RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> Login(LoginInputModel input)
    {
        input.ReturnUrl = SafeReturnUrl(input.ReturnUrl);
        // Preview query strings never become protocol context or a real sign-in flow.
        if (Request.Query.ContainsKey("preview") || Request.Query.ContainsKey("app")) return BadRequest();
        var validated = input.OidcContext is not null ? await oidcContext.ReadAsync(input.OidcContext) : null;
        if (input.OidcContext is not null && validated is null) return InvalidContext();
        if (ModelState.IsValid)
        {
            var user = await signIn.UserManager.FindByNameAsync(input.Email.Trim());
            // AIS3 has no two-factor challenge yet. Never downgrade an imported
            // account with two-factor enabled to password-only authentication.
            if (user is not null && !user.TwoFactorEnabled)
            {
                var result = await signIn.PasswordSignInAsync(user, input.Password, input.RememberMe, lockoutOnFailure: true);
                if (result.Succeeded) return LocalRedirect(validated is null ? input.ReturnUrl : OidcLoginContext.AfterPasswordSignIn(validated.Request));
            }
            ModelState.AddModelError(string.Empty, "Unable to sign in. Check your credentials and that your email is confirmed, or try again later.");
        }
        input.Password = string.Empty;
        ModelState.SetModelValue(nameof(input.Password), string.Empty, string.Empty);
        ModelState.Remove(nameof(input.ReturnUrl));
        return View("Account", new AccountPageModel("Login", validated?.ApplicationName, input.OidcContext) { Login = input });
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
    public Task<IActionResult> Register(string? context) => PreviewPage("Register", context);

    [HttpGet("ForgotPassword")]
    public Task<IActionResult> ForgotPassword(string? context) => PreviewPage("ForgotPassword", context);

    private async Task<IActionResult> PreviewPage(string page, string? context)
    {
        var validated = context is not null ? await oidcContext.ReadAsync(context) : null;
        if (context is not null && validated is null) return InvalidContext();
        return View("Account", new AccountPageModel(page, validated?.ApplicationName, context));
    }

    private IActionResult InvalidContext()
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("Account", new AccountPageModel("InvalidRequest"));
    }
}
