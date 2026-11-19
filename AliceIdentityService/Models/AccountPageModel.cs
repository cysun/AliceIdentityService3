namespace AliceIdentityService.Models;

public sealed record AccountPageModel(string Page, string? ApplicationName = null, string? OidcContext = null)
{
    public LoginInputModel Login { get; init; } = new();
    public RegistrationInputModel Registration { get; init; } = new();
    public ResendConfirmationInputModel Resend { get; init; } = new();
    public AccountEmailLinkModel PasswordLink { get; init; } = new();
    public ForgotPasswordInputModel ForgotPassword { get; init; } = new();
    public ChangePasswordInputModel ChangePassword { get; init; } = new();
    public ProfileInputModel Profile { get; init; } = new();
    public bool ProfileSaved { get; init; }
    public bool ContinuationExpired { get; init; }
    public string? ReturnUrl { get; init; }
    public IReadOnlyList<KeyValuePair<string, string>> LogoutParameters { get; init; } = [];
    public bool IsOidc => ApplicationName is not null;
    public string Heading => Page switch
    {
        "Register" => "Create your account",
        "ResendConfirmation" => "Resend confirmation email",
        "CheckEmail" => "Check your email",
        "ConfirmEmail" => "Confirm your email",
        "EmailConfirmed" => "Email confirmed",
        "ConfirmationInvalid" => "Unable to confirm email",
        "MailUnavailable" => "Email is temporarily unavailable",
        "ForgotPassword" => "Forgot your password?",
        "ResetEmailSent" => "Check your email",
        "ResetPassword" => "Choose a new password",
        "ResetInvalid" => "Unable to reset password",
        "PasswordReset" => "Password reset",
        "ChangePassword" => "Change your password",
        "PasswordChanged" => "Password changed",
        "Home" => "My profile",
        "Logout" or "OidcLogout" => "Sign out?",
        "InvalidRequest" => "Unable to continue",
        "AccessDenied" => "Access denied",
        _ => IsOidc ? "Sign in" : "Welcome back"
    };
}
