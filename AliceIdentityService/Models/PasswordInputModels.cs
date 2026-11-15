using System.ComponentModel.DataAnnotations;

namespace AliceIdentityService.Models;

public sealed class ForgotPasswordInputModel
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;
    [StringLength(20000)]
    public string? OidcContext { get; set; }
}

public class ResetPasswordLinkModel
{
    [Required, StringLength(256)]
    public string UserId { get; set; } = string.Empty;
    [Required, StringLength(4000)]
    public string Code { get; set; } = string.Empty;
    [StringLength(20000)]
    public string? OidcContext { get; set; }
}

public sealed class ResetPasswordInputModel : ResetPasswordLinkModel
{
    [Required, StringLength(4096), Display(Name = "New password")]
    public string Password { get; set; } = string.Empty;
    [Required, StringLength(4096), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class ChangePasswordInputModel
{
    [Required, StringLength(4096), Display(Name = "Current password")]
    public string CurrentPassword { get; set; } = string.Empty;
    [Required, StringLength(4096), Display(Name = "New password")]
    public string Password { get; set; } = string.Empty;
    [Required, StringLength(4096), Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
}
