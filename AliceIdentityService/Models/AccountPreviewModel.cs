namespace AliceIdentityService.Models;

public sealed record AccountPreviewModel(string Page, string? ApplicationName, string? AppKey)
{
    public bool IsOidc => ApplicationName is not null;
    public string Heading => Page switch
    {
        "Register" => "Create your account",
        "ForgotPassword" => "Forgot your password?",
        _ => IsOidc ? "Sign in" : "Welcome back"
    };
}
