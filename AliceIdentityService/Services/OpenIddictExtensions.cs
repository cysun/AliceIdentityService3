using System.Security.Cryptography.X509Certificates;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;
using OpenIddict.Server;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.IdentityModel.Tokens;

namespace AliceIdentityService.Services;

public static class OpenIddictExtensions
{
    public static IServiceCollection AddAisOpenIddict(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddScoped<OidcLoginContext>();
        services.AddScoped<OidcPrincipalService>();
        services.AddCors();
        services.AddScoped<ICorsPolicyProvider, OidcCorsPolicyProvider>();
        services.AddOpenIddict().AddServer(options =>
        {
            options.SetAuthorizationEndpointUris("connect/authorize")
                .SetTokenEndpointUris("connect/token")
                .SetUserInfoEndpointUris("connect/userinfo")
                .SetEndSessionEndpointUris("connect/logout");
            options.AllowAuthorizationCodeFlow().AllowRefreshTokenFlow();
            // AIS2 clients have grant/response/scope permissions, but no endpoint permissions.
            options.IgnoreEndpointPermissions();
            options.RegisterScopes(Scopes.Email, Scopes.Profile, Scopes.Address, Scopes.Phone);
            options.Configure(server => server.CodeChallengeMethods.Remove(CodeChallengeMethods.Plain));
            options.SetAuthorizationCodeLifetime(TimeSpan.FromMinutes(5));
            options.SetAccessTokenLifetime(TimeSpan.FromHours(1));
            options.SetIdentityTokenLifetime(TimeSpan.FromHours(1));
            options.SetRefreshTokenLifetime(TimeSpan.FromDays(14));
            options.SetRefreshTokenReuseLeeway(TimeSpan.Zero);
            options.DisableSlidingRefreshTokenExpiration();
            options.AddEventHandler<OpenIddictServerEvents.ProcessSignInContext>(handler => handler
                .UseScopedHandler<ApplyScopeResources>()
                .SetOrder(OpenIddictServerHandlers.PrepareAccessTokenPrincipal.Descriptor.Order - 1));
            // Retain AIS2's signed JWT access tokens for independently validating APIs.
            options.DisableAccessTokenEncryption();
            options.UseAspNetCore().EnableAuthorizationEndpointPassthrough()
                .EnableTokenEndpointPassthrough().EnableUserInfoEndpointPassthrough()
                .EnableEndSessionEndpointPassthrough();

            if (environment.IsEnvironment("Testing"))
            {
                options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
            }
            else if (environment.IsDeployed())
            {
                // The persistent certificates are added through DI below.
            }
            else if (!string.IsNullOrWhiteSpace(configuration["OpenIddict:SigningCertificatePath"]) &&
                !string.IsNullOrWhiteSpace(configuration["OpenIddict:EncryptionCertificatePath"]))
            {
                options.AddSigningCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                    Path.GetFullPath(configuration["OpenIddict:SigningCertificatePath"]!, environment.ContentRootPath),
                    configuration["OpenIddict:SigningCertificatePassword"]));
                options.AddEncryptionCertificate(X509CertificateLoader.LoadPkcs12FromFile(
                    Path.GetFullPath(configuration["OpenIddict:EncryptionCertificatePath"]!, environment.ContentRootPath),
                    configuration["OpenIddict:EncryptionCertificatePassword"]));
            }
            else if (!string.IsNullOrWhiteSpace(configuration["OpenIddict:SigningCertificatePath"]) ||
                !string.IsNullOrWhiteSpace(configuration["OpenIddict:EncryptionCertificatePath"]))
                throw new InvalidOperationException("Configure both OpenIddict certificate paths, or leave both empty for development certificates.");
            else if (environment.IsDevelopment())
            {
                options.AddDevelopmentEncryptionCertificate().AddDevelopmentSigningCertificate();
            }
            else throw new InvalidOperationException("Configure both OpenIddict signing and encryption certificates before deployment.");
        });
        if (environment.IsDeployed())
            services.AddOptions<OpenIddictServerOptions>().Configure<DeploymentCertificates>((options, certificates) =>
            {
                options.SigningCredentials.Add(new SigningCredentials(new X509SecurityKey(certificates.Signing), SecurityAlgorithms.RsaSha256));
                options.EncryptionCredentials.Add(new EncryptingCredentials(new X509SecurityKey(certificates.Encryption), SecurityAlgorithms.RsaOAEP, SecurityAlgorithms.Aes256CbcHmacSha512));
            });
        services.AddOptions<OpenIddictServerOptions>().Configure<IConfiguration>((options, settings) =>
        {
            var issuer = new Uri(settings["OpenIddict:Issuer"] ?? "https://localhost:7443/", UriKind.Absolute);
            if (issuer.Scheme != "https" || !string.IsNullOrEmpty(issuer.Query) || !string.IsNullOrEmpty(issuer.Fragment) || !string.IsNullOrEmpty(issuer.UserInfo))
                throw new InvalidOperationException("OpenIddict:Issuer must be an HTTPS URL without user information, query, or fragment.");
            options.Issuer = issuer;
        });
        return services;
    }

    private sealed class ApplyScopeResources(IOpenIddictScopeManager scopes)
        : IOpenIddictServerHandler<OpenIddictServerEvents.ProcessSignInContext>
    {
        public async ValueTask HandleAsync(OpenIddictServerEvents.ProcessSignInContext context)
        {
            // Replace even an empty set after OpenIddict restores private claims from the grant.
            var principal = context.Principal!;
            var resources = new List<string>();
            await foreach (var resource in scopes.ListResourcesAsync(principal.GetScopes(), context.CancellationToken))
                resources.Add(resource);
            principal.SetResources(resources);
        }
    }
}
