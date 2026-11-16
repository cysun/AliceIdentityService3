using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AliceIdentityService.Models;

public class AdminUserVersionInput
{
    [Required, RegularExpression("^[A-F0-9]{64}$")]
    public string Version { get; set; } = string.Empty;
}

public sealed class AdminCreateUserInput
{
    [Required, StringLength(255)] public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(255)] public string LastName { get; set; } = string.Empty;
    [Required, StringLength(255)] public string ScreenName { get; set; } = string.Empty;
    [Required, EmailAddress, StringLength(256)] public string Email { get; set; } = string.Empty;
    [Required, StringLength(4096)] public string Password { get; set; } = string.Empty;
    [Required, StringLength(4096), Compare(nameof(Password))] public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class AdminEditUserInput : AdminUserVersionInput
{
    [Required, StringLength(255)] public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(255)] public string LastName { get; set; } = string.Empty;
    [Required, StringLength(255)] public string ScreenName { get; set; } = string.Empty;
    [JsonRequired] public bool EmailConfirmed { get; set; }
}

public sealed class AdminPasswordInput : AdminUserVersionInput
{
    [Required, StringLength(4096)] public string Password { get; set; } = string.Empty;
    [Required, StringLength(4096), Compare(nameof(Password))] public string ConfirmPassword { get; set; } = string.Empty;
}

public sealed class AdminLockInput : AdminUserVersionInput
{
    [Required, RegularExpression("^(hour|day|indefinite)$")]
    public string Duration { get; set; } = "day";
}

public sealed class AdminDeleteUserInput : AdminUserVersionInput
{
    [Required, StringLength(1024)] public string Confirmation { get; set; } = string.Empty;
}

public sealed record AdminMutationResult(string Id, bool SessionEnded);
