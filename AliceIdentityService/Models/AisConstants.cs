namespace AliceIdentityService.Models;

public static class AisConstants
{
    public const string AdminClaim = "ais_admin";
    public const string AdminPolicy = "Administrator";

    public static IReadOnlyDictionary<string, string[]> StandardScopes { get; } = new Dictionary<string, string[]>
    {
        ["openid"] = ["sub"],
        ["email"] = ["email", "email_verified"],
        ["address"] = ["address"],
        ["profile"] = ["name", "family_name", "given_name", "middle_name", "nickname", "preferred_username", "profile",
            "picture", "website", "gender", "birthdate", "zoneinfo", "locale", "updated_at"],
        ["phone"] = ["phone_number", "phone_number_verified"]
    };
}
