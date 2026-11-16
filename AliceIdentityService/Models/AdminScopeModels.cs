using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AliceIdentityService.Models;

public sealed class AdminScopeQuery
{
    [StringLength(200)] public string? Q { get; set; }
    [Range(1, 10000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 25;
}

public class AdminScopeInput
{
    [Required, StringLength(200)] public string Name { get; set; } = string.Empty;
    [StringLength(200)] public string? DisplayName { get; set; }
    [StringLength(2000)] public string? Description { get; set; }
    [Required, MaxLength(100), JsonRequired] public string[] Claims { get; set; } = [];
    [Required, MaxLength(100), JsonRequired] public string[] Resources { get; set; } = [];
}

public sealed class AdminEditScopeInput : AdminScopeInput
{
    [Required, RegularExpression("^[A-F0-9]{64}$")] public string Version { get; set; } = string.Empty;
}

public sealed record AdminScopeDetail(string Id, string? Name, string? DisplayName, string? Description,
    IReadOnlyList<string> Claims, IReadOnlyList<string> Resources, bool Standard, string Version);
public sealed record AdminScopePage(IReadOnlyList<AdminScopeDetail> Items, long TotalCount, int Page, int PageSize);
