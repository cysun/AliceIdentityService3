namespace AliceIdentityService.Models;

public sealed record AccountPageModel(string Page, string? ApplicationName = null, string? AppKey = null)
{
    public LoginInputModel Login { get; init; } = new();
    public string? ReturnUrl { get; init; }
    public bool IsOidc => ApplicationName is not null;
    public string Heading => Page switch
    {
        "Register" => "Create your account",
        "ForgotPassword" => "Forgot your password?",
        "Home" => "You're signed in",
        "Logout" => "Sign out?",
        "AccessDenied" => "Access denied",
        _ => IsOidc ? "Sign in" : "Welcome back"
    };
}
