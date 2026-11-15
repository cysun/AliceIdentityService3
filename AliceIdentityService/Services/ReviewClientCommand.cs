using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Services;

// Explicit, development-only setup for the separate manual-review client. Never runs at web startup.
public static class ReviewClientCommand
{
    public static async Task<int> RunAsync(IServiceProvider services, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
        {
            Console.Error.WriteLine("The review client can only be registered in Development. Use a test database.");
            return 1;
        }
        await using var scope = services.CreateAsyncScope();
        var applications = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        var descriptor = new OpenIddictApplicationDescriptor
        {
            ClientId = "ais3-review",
            DisplayName = "AIS3 Review Client",
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            RedirectUris = { new Uri("https://localhost:9443/signin-oidc") },
            PostLogoutRedirectUris = { new Uri("https://localhost:9443/signout-callback-oidc") },
            Permissions = { Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                Permissions.ResponseTypes.Code, Permissions.Scopes.Email, Permissions.Scopes.Profile },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        };
        if (await applications.FindByClientIdAsync(descriptor.ClientId) is { } existing)
        {
            if (!await applications.HasClientTypeAsync(existing, ClientTypes.Public) ||
                !await applications.HasConsentTypeAsync(existing, ConsentTypes.Implicit) ||
                !(await applications.GetRedirectUrisAsync(existing)).ToHashSet().SetEquals(descriptor.RedirectUris.Select(uri => uri.AbsoluteUri)) ||
                !(await applications.GetPostLogoutRedirectUrisAsync(existing)).ToHashSet().SetEquals(descriptor.PostLogoutRedirectUris.Select(uri => uri.AbsoluteUri)) ||
                !(await applications.GetPermissionsAsync(existing)).ToHashSet().SetEquals(descriptor.Permissions) ||
                !(await applications.GetRequirementsAsync(existing)).ToHashSet().SetEquals(descriptor.Requirements))
            {
                Console.Error.WriteLine("ais3-review already exists with different settings; it was not changed. Use an isolated test database.");
                return 1;
            }
            Console.WriteLine("The matching AIS3 review client already exists; no changes made.");
        }
        else
        {
            await applications.CreateAsync(descriptor);
            Console.WriteLine("Created public PKCE client ais3-review for https://localhost:9443.");
        }
        return 0;
    }
}
