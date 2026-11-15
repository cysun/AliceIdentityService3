using AliceIdentityService.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.RateLimiting;

namespace AliceIdentityService.Controllers;

[Route("Account")]
[ResponseCache(NoStore = true, Location = ResponseCacheLocation.None)]
public sealed class AccountController(SignInManager<User> signIn, OidcLoginContext oidcContext,
    RegistrationService registration, PasswordRecoveryService recovery) : Controller
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

    [Authorize, HttpGet(""), HttpGet("Profile")]
    public async Task<IActionResult> Home(bool saved = false)
    {
        var user = await signIn.UserManager.GetUserAsync(User);
        if (user is null) return Challenge();
        return View("Account", new AccountPageModel("Home")
        {
            Profile = new() { FirstName = user.FirstName, LastName = user.LastName, ScreenName = user.ScreenName, Email = user.Email },
            ProfileSaved = saved
        });
    }

    [Authorize, HttpPost("Profile"), RequestSizeLimit(16 * 1024)]
    public async Task<IActionResult> SaveProfile(ProfileInputModel input)
    {
        var user = await signIn.UserManager.GetUserAsync(User);
        if (user is null) return Challenge();
        input.Email = user.Email;
        if (ModelState.IsValid)
        {
            // Bind only the three editable names. The signed-in subject determines the account.
            user.FirstName = input.FirstName.Trim();
            user.LastName = input.LastName.Trim();
            user.ScreenName = input.ScreenName.Trim();
            var result = await signIn.UserManager.UpdateAsync(user);
            if (result.Succeeded) return RedirectToAction(nameof(Home), new { saved = true });
            ModelState.AddModelError(string.Empty, "Unable to save your profile. Reload the page and try again.");
        }
        // Claims are refreshed on the next request without renewing the fixed cookie expiry.
        return View("Account", new AccountPageModel("Home") { Profile = input });
    }

    [Authorize, HttpGet("ChangePassword")]
    public IActionResult ChangePassword() => View("Account", new AccountPageModel("ChangePassword"));

    [Authorize, HttpPost("ChangePassword"), RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> ChangePassword(ChangePasswordInputModel input)
    {
        if (ModelState.IsValid)
        {
            var user = await signIn.UserManager.GetUserAsync(User);
            if (user is null) return Challenge();
            var check = await signIn.CheckPasswordSignInAsync(user, input.CurrentPassword, lockoutOnFailure: true);
            if (check.Succeeded)
            {
                var result = await signIn.UserManager.ChangePasswordAsync(user, input.CurrentPassword, input.Password);
                if (result.Succeeded)
                {
                    await signIn.SignOutAsync();
                    return View("Account", new AccountPageModel("PasswordChanged"));
                }
                foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            }
            else ModelState.AddModelError(string.Empty, "Unable to change your password. Check your current password or try again later.");
        }
        input.CurrentPassword = input.Password = input.ConfirmPassword = string.Empty;
        ClearPasswords(nameof(input.CurrentPassword), nameof(input.Password), nameof(input.ConfirmPassword));
        return View("Account", new AccountPageModel("ChangePassword") { ChangePassword = input });
    }

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
    public async Task<IActionResult> Register(string? context)
    {
        var validated = context is not null ? await oidcContext.ReadAsync(context) : null;
        if (context is not null && validated is null) return InvalidContext();
        return View("Account", new AccountPageModel("Register", validated?.ApplicationName, context)
        {
            Registration = new() { OidcContext = context }
        });
    }

    [HttpPost("Register"), RequestSizeLimit(48 * 1024), EnableRateLimiting("account-email")]
    public async Task<IActionResult> Register(RegistrationInputModel input)
    {
        var validated = input.OidcContext is not null ? await oidcContext.ReadAsync(input.OidcContext) : null;
        if (input.OidcContext is not null && validated is null) return InvalidContext();
        if (ModelState.IsValid)
        {
            try
            {
                var result = await registration.RegisterAsync(input, HttpContext.RequestAborted);
                if (result.Succeeded) return CheckEmail(validated, input.OidcContext);
                foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
            }
            catch (MailDeliveryException) { return MailUnavailable(validated, input.OidcContext); }
        }
        input.Password = input.ConfirmPassword = string.Empty;
        ModelState.SetModelValue(nameof(input.Password), string.Empty, string.Empty);
        ModelState.SetModelValue(nameof(input.ConfirmPassword), string.Empty, string.Empty);
        return View("Account", new AccountPageModel("Register", validated?.ApplicationName, input.OidcContext) { Registration = input });
    }

    [HttpGet("ResendConfirmation")]
    public async Task<IActionResult> ResendConfirmation(string? context)
    {
        var validated = context is not null ? await oidcContext.ReadAsync(context) : null;
        if (context is not null && validated is null) return InvalidContext();
        return View("Account", new AccountPageModel("ResendConfirmation", validated?.ApplicationName, context)
        {
            Resend = new() { OidcContext = context }
        });
    }

    [HttpPost("ResendConfirmation"), RequestSizeLimit(32 * 1024), EnableRateLimiting("account-email")]
    public async Task<IActionResult> ResendConfirmation(ResendConfirmationInputModel input)
    {
        var validated = input.OidcContext is not null ? await oidcContext.ReadAsync(input.OidcContext) : null;
        if (input.OidcContext is not null && validated is null) return InvalidContext();
        if (!ModelState.IsValid)
            return View("Account", new AccountPageModel("ResendConfirmation", validated?.ApplicationName, input.OidcContext) { Resend = input });
        try { await registration.ResendAsync(input.Email, input.OidcContext, HttpContext.RequestAborted); }
        catch (MailDeliveryException) { return MailUnavailable(validated, input.OidcContext); }
        return CheckEmail(validated, input.OidcContext);
    }

    [HttpGet("ConfirmEmail")]
    public async Task<IActionResult> ConfirmEmail([FromQuery] ConfirmEmailInputModel input)
    {
        if (!ModelState.IsValid || !await registration.CanConfirmAsync(input)) return ConfirmationInvalid();
        var validated = input.OidcContext is not null ? await oidcContext.ReadAsync(input.OidcContext) : null;
        return View("Account", new AccountPageModel("ConfirmEmail", validated?.ApplicationName, validated is null ? null : input.OidcContext)
        {
            Confirmation = input
        });
    }

    [HttpPost("ConfirmEmail"), ActionName("ConfirmEmail"), RequestSizeLimit(32 * 1024)]
    public async Task<IActionResult> ConfirmEmailSubmit(ConfirmEmailInputModel input)
    {
        if (!ModelState.IsValid || !await registration.ConfirmAsync(input, HttpContext.RequestAborted)) return ConfirmationInvalid();
        var validated = input.OidcContext is not null ? await oidcContext.ReadAsync(input.OidcContext) : null;
        return View("Account", new AccountPageModel("EmailConfirmed", validated?.ApplicationName, validated is null ? null : input.OidcContext)
        {
            ContinuationExpired = input.OidcContext is not null && validated is null
        });
    }

    private IActionResult CheckEmail(LoginContextDetails? validated, string? context) =>
        View("Account", new AccountPageModel("CheckEmail", validated?.ApplicationName, context));

    private IActionResult MailUnavailable(LoginContextDetails? validated, string? context)
    {
        Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        Response.Headers.RetryAfter = "60";
        return View("Account", new AccountPageModel("MailUnavailable", validated?.ApplicationName, context));
    }

    private IActionResult ConfirmationInvalid()
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("Account", new AccountPageModel("ConfirmationInvalid"));
    }

    [HttpGet("ForgotPassword")]
    public async Task<IActionResult> ForgotPassword(string? context)
    {
        var validated = context is not null ? await oidcContext.ReadAsync(context) : null;
        if (context is not null && validated is null) return InvalidContext();
        return View("Account", new AccountPageModel("ForgotPassword", validated?.ApplicationName, context)
        {
            ForgotPassword = new() { OidcContext = context }
        });
    }

    [HttpPost("ForgotPassword"), RequestSizeLimit(32 * 1024), EnableRateLimiting("account-email")]
    public async Task<IActionResult> ForgotPassword(ForgotPasswordInputModel input)
    {
        var validated = input.OidcContext is not null ? await oidcContext.ReadAsync(input.OidcContext) : null;
        if (input.OidcContext is not null && validated is null) return InvalidContext();
        if (!ModelState.IsValid)
            return View("Account", new AccountPageModel("ForgotPassword", validated?.ApplicationName, input.OidcContext) { ForgotPassword = input });
        try { await recovery.RequestAsync(input.Email, input.OidcContext, HttpContext.RequestAborted); }
        catch (MailDeliveryException)
        {
            // The sender logs a sanitized failure. A different response during an outage
            // would reveal whether the address belongs to a confirmed account.
        }
        return View("Account", new AccountPageModel("ResetEmailSent", validated?.ApplicationName, input.OidcContext));
    }

    [HttpGet("ResetPassword")]
    public async Task<IActionResult> ResetPassword([FromQuery] ResetPasswordLinkModel link)
    {
        if (!ModelState.IsValid || await recovery.FindResetUserAsync(link) is null) return ResetInvalid();
        return await ResetPage(new() { UserId = link.UserId, Code = link.Code, OidcContext = link.OidcContext });
    }

    [HttpPost("ResetPassword"), ActionName("ResetPassword"), RequestSizeLimit(48 * 1024)]
    public async Task<IActionResult> ResetPasswordSubmit(ResetPasswordInputModel input)
    {
        // Validate the link independently of password errors before redisplaying it.
        if (string.IsNullOrEmpty(input.UserId) || input.UserId.Length > 256 || string.IsNullOrEmpty(input.Code) ||
            input.Code.Length > 4000 || input.OidcContext?.Length > 20000 || await recovery.FindResetUserAsync(input) is null)
            return ResetInvalid();
        if (ModelState.IsValid)
        {
            var result = await recovery.ResetAsync(input);
            if (result.Succeeded)
            {
                // No automatic sign-in; clear this browser too, even when following a link while signed in.
                await signIn.SignOutAsync();
                var validated = input.OidcContext is not null ? await oidcContext.ReadAsync(input.OidcContext) : null;
                return View("Account", new AccountPageModel("PasswordReset", validated?.ApplicationName, validated is null ? null : input.OidcContext)
                {
                    ContinuationExpired = input.OidcContext is not null && validated is null
                });
            }
            if (result.Errors.Any(error => error.Code is "InvalidToken" or "ConcurrencyFailure")) return ResetInvalid();
            foreach (var error in result.Errors) ModelState.AddModelError(string.Empty, error.Description);
        }
        input.Password = input.ConfirmPassword = string.Empty;
        ClearPasswords(nameof(input.Password), nameof(input.ConfirmPassword));
        return await ResetPage(input);
    }

    private async Task<IActionResult> ResetPage(ResetPasswordInputModel input)
    {
        var validated = input.OidcContext is not null ? await oidcContext.ReadAsync(input.OidcContext) : null;
        return View("Account", new AccountPageModel("ResetPassword", validated?.ApplicationName, validated is null ? null : input.OidcContext)
        {
            ResetPassword = input
        });
    }

    private IActionResult ResetInvalid()
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("Account", new AccountPageModel("ResetInvalid"));
    }

    private void ClearPasswords(params string[] names)
    {
        foreach (var name in names) ModelState.SetModelValue(name, string.Empty, string.Empty);
    }

    private IActionResult InvalidContext()
    {
        Response.StatusCode = StatusCodes.Status400BadRequest;
        return View("Account", new AccountPageModel("InvalidRequest"));
    }
}
