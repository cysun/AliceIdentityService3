using System.Security.Claims;
using AliceIdentityService.Models;
using Microsoft.AspNetCore.Identity;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Services;

public sealed class OidcPrincipalService(SignInManager<User> signIn, IOpenIddictScopeManager scopes)
{
    public async Task<bool> CanSignInAsync(User user) => !user.TwoFactorEnabled &&
        await signIn.CanSignInAsync(user) && !await signIn.UserManager.IsLockedOutAsync(user);

    public async Task<ClaimsPrincipal> CreateAsync(User user, IEnumerable<string> grantedScopes, long authenticationTime)
    {
        var principal = await signIn.CreateUserPrincipalAsync(user);
        var identity = (ClaimsIdentity)principal.Identity!;
        foreach (var claim in identity.Claims.Where(claim => IsProtocolClaim(claim.Type)).ToArray()) identity.RemoveClaim(claim);
        principal.SetClaim(Claims.Subject, user.Id);
        // Preserve AIS2's name=email/username contract and mapped profile fields.
        principal.SetClaim(Claims.Name, user.UserName);
        principal.SetClaim(Claims.Email, user.Email);
        principal.SetClaim(Claims.EmailVerified, user.EmailConfirmed);
        principal.SetClaim(Claims.GivenName, user.FirstName);
        principal.SetClaim(Claims.FamilyName, user.LastName);
        principal.SetClaim(Claims.Nickname, user.ScreenName);
        principal.SetClaim(Claims.AuthenticationTime, authenticationTime);
        principal.SetScopes(grantedScopes);

        var identityClaims = new HashSet<string>(StringComparer.Ordinal);
        var accessClaims = new HashSet<string>(StringComparer.Ordinal);
        foreach (var name in principal.GetScopes())
        {
            if (AisConstants.StandardScopes.TryGetValue(name, out var standard))
            {
                identityClaims.UnionWith(standard);
                if (name == Scopes.Email) accessClaims.UnionWith(standard);
                if (name == Scopes.Profile) accessClaims.Add(Claims.Name);
            }
            else if (await scopes.FindByNameAsync(name) is { } scope)
            {
                var properties = await scopes.GetPropertiesAsync(scope);
                if (properties.TryGetValue("claims", out var values) && values.ValueKind == System.Text.Json.JsonValueKind.Array)
                    foreach (var value in values.EnumerateArray())
                        if (value.ValueKind == System.Text.Json.JsonValueKind.String && value.GetString() is { } type)
                        {
                            identityClaims.Add(type);
                            accessClaims.Add(type);
                        }
            }
        }
        foreach (var claim in principal.Claims)
        {
            if (claim.Type == signIn.Options.ClaimsIdentity.SecurityStampClaimType || AdminClaimRules.ProtocolOrSecret(claim.Type))
                claim.SetDestinations([]);
            else
                claim.SetDestinations(new[]
                {
                    identityClaims.Contains(claim.Type) ? Destinations.IdentityToken : null,
                    accessClaims.Contains(claim.Type) ? Destinations.AccessToken : null
                }.OfType<string>());
        }
        // OIDC requires auth_time when max_age is used; including it consistently
        // also lets clients enforce their own freshness policy after a continuation.
        principal.FindFirst(Claims.AuthenticationTime)!.SetDestinations(Destinations.IdentityToken);
        return principal;
    }

    private static bool IsProtocolClaim(string type) => type.StartsWith("oi_", StringComparison.Ordinal) ||
        type is "iss" or "aud" or "exp" or "iat" or "nbf" or "jti" or "nonce" or "auth_time" or "azp" or "scope";
}
