using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Identity;

namespace AliceIdentityService.Models;

// Persisted AIS2 contract: string IDs and the existing Identity password/stamp fields.
public sealed class User : IdentityUser
{
    [Required, MaxLength(255), PersonalData]
    public string FirstName { get; set; } = string.Empty;

    [Required, MaxLength(255), PersonalData]
    public string LastName { get; set; } = string.Empty;

    [Required, MaxLength(255)]
    public string ScreenName { get; set; } = string.Empty;

    public DateTime CreationTime { get; set; } = DateTime.UtcNow;
    public string FullName => $"{FirstName} {LastName}";
}
