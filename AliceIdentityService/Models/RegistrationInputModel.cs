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
