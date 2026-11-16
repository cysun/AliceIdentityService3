using System.Net;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Options;
using Npgsql;
using OpenIddict.Server;

namespace AliceIdentityService.Services;

public static class HostingExtensions
{
    public static bool IsDeployed(this IHostEnvironment environment) =>
        !environment.IsDevelopment() && !environment.IsEnvironment("Testing");

    public static IServiceCollection AddAisHosting(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        services.AddOptions<ForwardedHeadersOptions>().PostConfigure<IConfiguration>((options, configuration) =>
        {
            options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
            options.ForwardLimit = 1;
            options.KnownProxies.Clear();
            options.KnownIPNetworks.Clear();
            var proxies = configuration.GetSection("Hosting:TrustedProxies").Get<string[]>() ?? ["127.0.0.1", "::1"];
            if (proxies.Length == 0) throw new InvalidOperationException("Hosting:TrustedProxies must contain at least one proxy IP address when configured.");
            foreach (var value in proxies)
            {
                if (!IPAddress.TryParse(value, out var address) || address.Equals(IPAddress.Any) || address.Equals(IPAddress.IPv6Any))
                    throw new InvalidOperationException("Hosting:TrustedProxies accepts individual proxy IP addresses only.");
                options.KnownProxies.Add(address);
            }
        });
        services.AddOptions<Microsoft.AspNetCore.HttpsPolicy.HttpsRedirectionOptions>().Configure<IConfiguration>((options, configuration) =>
            options.HttpsPort = environment.IsDeployed() ? PublicOrigin(configuration).Port : configuration.GetValue("HTTPS_PORT", 7443));
        services.AddHsts(options => { options.MaxAge = TimeSpan.FromDays(180); options.IncludeSubDomains = false; options.Preload = false; });
        if (!environment.IsDeployed()) return services;

        ValidateDeploymentConfiguration(configuration, environment);
        var keyRingPath = Path.Combine(configuration["Application:CertificateFolder"]!, "keys");
        DirectoryInfo keyRingDirectory;
        try
        {
            keyRingDirectory = OperatingSystem.IsWindows()
                ? Directory.CreateDirectory(keyRingPath)
                : Directory.CreateDirectory(keyRingPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            throw new InvalidOperationException("The keys subfolder of Application:CertificateFolder must be readable and writable by the AIS3 service account.");
        }
        var certificates = new DeploymentCertificates(configuration);
        services.AddSingleton(_ => certificates);
        services.AddDataProtection().SetApplicationName("AliceIdentityService3." + environment.EnvironmentName)
            .PersistKeysToFileSystem(keyRingDirectory)
            // Reuse the token-encryption certificate to wrap the persistent Data Protection keys.
            .ProtectKeysWithCertificate(certificates.Encryption)
            .UnprotectKeysWithAnyCertificate(certificates.Encryption);
        return services;
    }

    public static Uri PublicOrigin(IConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration["OpenIddict:Issuer"], UriKind.Absolute, out var origin) || origin.Scheme != "https" ||
            origin.AbsolutePath != "/" || origin.Query != "" || origin.Fragment != "" || origin.UserInfo != "")
            throw new InvalidOperationException("OpenIddict:Issuer must be an explicit HTTPS origin for deployed hosting.");
        return origin;
    }

    private static void ValidateDeploymentConfiguration(IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsStaging() && !environment.IsProduction())
            throw new InvalidOperationException("Use Development, Testing, Staging or Production as the environment.");
        var origin = PublicOrigin(configuration);
        if (!Uri.TryCreate(configuration["Email:AppUrl"], UriKind.Absolute, out var emailOrigin) || emailOrigin != origin)
            throw new InvalidOperationException("Email:AppUrl must match OpenIddict:Issuer for deployed hosting.");
        // This host has one public origin. Do not accept wildcards or trust forwarded Host headers.
        if (!string.Equals(configuration["AllowedHosts"], origin.IdnHost, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("AllowedHosts must be the single public issuer hostname, without a port or wildcard.");
        var path = configuration["Application:CertificateFolder"];
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path) ||
            Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(environment.ContentRootPath) + Path.DirectorySeparatorChar, StringComparison.Ordinal) ||
            Path.GetFullPath(path) == Path.TrimEndingDirectorySeparator(environment.ContentRootPath))
            throw new InvalidOperationException("Application:CertificateFolder must be an absolute persistent directory outside the release content root.");
        try
        {
            var connection = new NpgsqlConnectionStringBuilder(configuration.GetConnectionString("DefaultConnection"));
            if (string.IsNullOrWhiteSpace(connection.Host) || string.IsNullOrWhiteSpace(connection.Database) || connection.IncludeErrorDetail || connection.LogParameters)
                throw new ArgumentException();
        }
        catch (ArgumentException) { throw new InvalidOperationException("Configure a database connection with a host and database; detailed errors and parameter logging must be disabled."); }

    }

    public static void ValidateAisHosting(this WebApplication app)
    {
        if (!app.Environment.IsDeployed()) return;
        _ = app.Services.GetRequiredService<IOptions<ForwardedHeadersOptions>>().Value;
        _ = app.Services.GetRequiredService<IOptions<EmailSettings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<RabbitMqSettings>>().Value;
        _ = app.Services.GetRequiredService<IOptions<OpenIddictServerOptions>>().Value;
        // Exercise persistence and decryption before accepting traffic. No database or RabbitMQ mutation occurs.
        var protector = app.Services.GetRequiredService<IDataProtectionProvider>().CreateProtector("AIS3.StartupCheck.v1");
        try
        {
            foreach (var key in app.Services.GetRequiredService<IKeyManager>().GetAllKeys().Where(key => !key.IsRevoked))
                _ = key.CreateEncryptor();
            if (protector.Unprotect(protector.Protect("startup")) != "startup") throw new InvalidOperationException();
        }
        catch { throw new InvalidOperationException("The persistent Data Protection key ring could not be read, written or decrypted. Check directory access and restore the original encryption-certificate.pfx if it was lost or replaced."); }
    }
}
