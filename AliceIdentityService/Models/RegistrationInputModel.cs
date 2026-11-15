using System.ComponentModel.DataAnnotations;

namespace AliceIdentityService.Models;

public sealed class RegistrationInputModel
{
    [Required, StringLength(255)]
    public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(255)]
    public string LastName { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;
    [Required, StringLength(4096)]
    public string Password { get; set; } = string.Empty;
    [Required, Compare(nameof(Password), ErrorMessage = "The passwords do not match.")]
    public string ConfirmPassword { get; set; } = string.Empty;
    [StringLength(20000)]
    public string? OidcContext { get; set; }
}

public sealed class ResendConfirmationInputModel
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;
    [StringLength(20000)]
    public string? OidcContext { get; set; }
}

public sealed class ConfirmEmailInputModel
{
    [Required, StringLength(256)]
    public string UserId { get; set; } = string.Empty;
    [Required, StringLength(4000)]
    public string Code { get; set; } = string.Empty;
    [StringLength(20000)]
    public string? OidcContext { get; set; }
}
