using AliceIdentityService.Web.Models;
using Microsoft.AspNetCore.Mvc;

namespace AliceIdentityService.Web.Controllers;

[Route("Account")]
public sealed class AccountController : Controller
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
    public IActionResult Login(string? preview, string? app) => AccountPage("Login", preview, app);

    [HttpGet("Register")]
    public IActionResult Register(string? preview, string? app) => AccountPage("Register", preview, app);

    [HttpGet("ForgotPassword")]
    public IActionResult ForgotPassword(string? preview, string? app) => AccountPage("ForgotPassword", preview, app);

    private IActionResult AccountPage(string page, string? preview, string? app)
    {
        var knownApp = app is not null && PreviewApplications.ContainsKey(app);
        var oidc = preview == "oidc" && knownApp;
        return View("Account", new AccountPreviewModel(page, oidc ? PreviewApplications[app!] : null, oidc ? app : null));
    }
}
