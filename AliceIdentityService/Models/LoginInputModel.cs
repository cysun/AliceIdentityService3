using System.ComponentModel.DataAnnotations;

namespace AliceIdentityService.Models;

public sealed class LoginInputModel
{
    [Required, EmailAddress, StringLength(256)]
    public string Email { get; set; } = string.Empty;

    [Required, StringLength(4096), DataType(DataType.Password)]
    public string Password { get; set; } = string.Empty;

    public bool RememberMe { get; set; }
    public string? ReturnUrl { get; set; }
    [StringLength(20000)]
    public string? OidcContext { get; set; }
}
