using System.Security.Cryptography.X509Certificates;
using static OpenIddict.Abstractions.OpenIddictConstants;
using OpenIddict.Server;
using Microsoft.AspNetCore.Cors.Infrastructure;

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
            // Retain AIS2's signed JWT access tokens for independently validating APIs.
            options.DisableAccessTokenEncryption();
            options.UseAspNetCore().EnableAuthorizationEndpointPassthrough()
                .EnableTokenEndpointPassthrough().EnableUserInfoEndpointPassthrough()
                .EnableEndSessionEndpointPassthrough();

            if (environment.IsEnvironment("Testing"))
            {
                options.AddEphemeralEncryptionKey().AddEphemeralSigningKey();
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
            else throw new InvalidOperationException("AIS3 is development-only until staging and hardening are reviewed. Configure both OpenIddict signing and encryption certificates before deployment.");
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
}
