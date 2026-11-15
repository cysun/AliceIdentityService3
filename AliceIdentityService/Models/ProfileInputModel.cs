using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc.ModelBinding;

namespace AliceIdentityService.Models;

public sealed class ProfileInputModel
{
    [Required, StringLength(255), Display(Name = "First name")]
    public string FirstName { get; set; } = string.Empty;
    [Required, StringLength(255), Display(Name = "Last name")]
    public string LastName { get; set; } = string.Empty;
    [Required, StringLength(255), Display(Name = "Screen name")]
    public string ScreenName { get; set; } = string.Empty;

    // Display only. Always loaded from the current account, never accepted from a form.
    [BindNever]
    public string? Email { get; set; }
}
