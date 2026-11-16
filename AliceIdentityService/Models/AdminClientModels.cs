using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace AliceIdentityService.Models;

public sealed class AdminClientQuery
{
    [StringLength(200)] public string? Q { get; set; }
    [Range(1, 10000)] public int Page { get; set; } = 1;
    [Range(1, 100)] public int PageSize { get; set; } = 25;
}

public class AdminClientSettingsInput
{
    [Required, StringLength(100)] public string ClientId { get; set; } = string.Empty;
    [Required, StringLength(200)] public string DisplayName { get; set; } = string.Empty;
    [Required, MinLength(1), MaxLength(100), JsonRequired] public string[] RedirectUris { get; set; } = [];
    [Required, MaxLength(100), JsonRequired] public string[] PostLogoutRedirectUris { get; set; } = [];
    [Required, MaxLength(100), JsonRequired] public string[] Scopes { get; set; } = [];
    [JsonRequired] public bool AllowRefreshTokens { get; set; } = true;
    [JsonRequired] public bool RequirePkce { get; set; } = true;
}

public sealed class AdminCreateClientInput : AdminClientSettingsInput
{
    [Required, RegularExpression("^(public|confidential)$")] public string ClientType { get; set; } = "public";
    [StringLength(256)] public string? ClientSecret { get; set; }
}

public sealed class AdminEditClientInput : AdminClientSettingsInput
{
    [Required, RegularExpression("^[A-F0-9]{64}$")] public string Version { get; set; } = string.Empty;
}

public sealed class AdminClientCredentialsInput : AdminUserVersionInput
{
    [Required, RegularExpression("^(public|confidential)$")] public string ClientType { get; set; } = string.Empty;
    [StringLength(256)] public string? ClientSecret { get; set; }
    [Required, StringLength(100)] public string Confirmation { get; set; } = string.Empty;
}

public sealed record AdminClientSummary(string Id, string? ClientId, string? DisplayName, string? ClientType,
    bool HasSecret, bool RequirePkce, IReadOnlyList<string> Scopes);
public sealed record AdminClientPage(IReadOnlyList<AdminClientSummary> Items, long TotalCount, int Page, int PageSize);
public sealed record AdminClientDetail(AdminClientSummary Client, string? ApplicationType, string? ConsentType,
    IReadOnlyList<string> RedirectUris, IReadOnlyList<string> PostLogoutRedirectUris, bool AllowRefreshTokens,
    bool CanManage, string Version);
