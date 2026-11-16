using System.Security.Claims;

namespace AliceIdentityService.Services;

public static class AdminClaimRules
{
    public static bool ValidType(string type) => type.Length is > 0 and <= 256 && !type.Any(char.IsWhiteSpace) && !type.Any(char.IsControl);
    public static bool ProtocolOrSecret(string type) => type.StartsWith("oi_", StringComparison.Ordinal) ||
        type == "AspNet.Identity.SecurityStamp" || type is "iss" or "aud" or "exp" or "iat" or "nbf" or "jti" or "nonce" or
        "auth_time" or "azp" or "scope" or "at_hash" or "c_hash" or "s_hash" or "cnf" or "sid" or "amr" or "acr" or
        "act" or "may_act" or "authorization_details" or "token_type";
    public static bool ManagedByIdentity(string type) => ProtocolOrSecret(type) ||
        type is "sub" or "name" or "email" or "email_verified" or "given_name" or "family_name" or "nickname" ||
        type == ClaimTypes.NameIdentifier || type == ClaimTypes.Name || type == ClaimTypes.Email;
}
