using System.ComponentModel.DataAnnotations;

namespace AliceIdentityService.Models;

public sealed class AdminUserQuery
{
    [StringLength(256)] public string? Q { get; set; }
    [RegularExpression("^(Confirmed|Unconfirmed|Locked)$")] public string? Status { get; set; }
    [RegularExpression("^(name|status|joined)$")] public string Sort { get; set; } = "name";
    [RegularExpression("^(asc|desc)$")] public string Direction { get; set; } = "asc";
    [Range(1, 10000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 25;
}

public sealed record AdminUserSummary(string Id, string Name, string? Email, string Status,
    DateTime CreatedAt, bool Administrator);
public sealed record AdminUserPage(IReadOnlyList<AdminUserSummary> Items, long TotalCount,
    int Page, int PageSize, DateTimeOffset AsOf);
public sealed record AdminUserClaim(string? Type, string? Value, int Id);
public sealed record AdminUserSecurity(bool HasPassword, bool TwoFactorEnabled, bool LockoutEnabled,
    DateTimeOffset? LockoutEnd, int AccessFailedCount);
public sealed record AdminUserDetail(AdminUserSummary User, string FirstName, string LastName,
    string ScreenName, bool EmailConfirmed, AdminUserSecurity Security, IReadOnlyList<AdminUserClaim> Claims, string Version);
