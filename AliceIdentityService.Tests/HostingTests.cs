using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Tests;

public sealed class HostingKeys : IDisposable
{
    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "ais3-hosting-" + Guid.NewGuid().ToString("N"));
    public Dictionary<string, string?> Settings { get; }
    public string KeyRingDirectory => Path.Combine(Settings["Application:CertificateFolder"]!, "keys");
    public HostingKeys()
    {
        Directory.CreateDirectory(DirectoryPath);
        Settings = new()
        {
            ["OpenIddict:Issuer"] = "https://stage.example.test/",
            ["Email:AppUrl"] = "https://stage.example.test/",
            ["Email:SenderEmail"] = "noreply@example.test",
            ["Email:SenderName"] = "Staging tests",
            ["AllowedHosts"] = "stage.example.test",
            ["Application:CertificateFolder"] = Path.Combine(DirectoryPath, "certificates")
        };
    }

    public string Certificate(string name, X509KeyUsageFlags usage, int days = 10, string? password = null)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=AIS3 test " + name, rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509KeyUsageExtension(usage, true));
        using var certificate = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-2), DateTimeOffset.UtcNow.AddDays(days));
        var folder = Settings["Application:CertificateFolder"]!;
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name + ".pfx");
        File.WriteAllBytes(path, certificate.Export(X509ContentType.Pfx, password));
        return path;
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}

internal sealed class HostingLog : ILogger<HostingSecurityMiddleware>
{
    public List<string> Lines { get; } = [];
    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
    public bool IsEnabled(LogLevel level) => true;
    public void Log<TState>(LogLevel level, EventId id, TState state, Exception? error, Func<TState, Exception?, string> format) => Lines.Add(format(state, error) + error);
}

internal sealed class StagingFactory(Dictionary<string, string?> settings, string environment = "Staging") : PreviewFactory
{
    private readonly string contentRoot = Path.Combine(Path.GetTempPath(), "ais3-stage-host-" + Guid.NewGuid().ToString("N"));
    public HostingLog Log { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        Directory.CreateDirectory(contentRoot);
        var startupSettings = new Dictionary<string, string?>(settings);
        startupSettings.TryAdd("ConnectionStrings:DefaultConnection", "Host=127.0.0.1;Port=1;Database=ais3_unavailable;Username=test;Timeout=1;Command Timeout=1");
        File.WriteAllText(Path.Combine(contentRoot, "appsettings.json"), JsonSerializer.Serialize(startupSettings));
        builder.UseContentRoot(contentRoot);
        builder.UseEnvironment(environment);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(settings));
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<ILogger<HostingSecurityMiddleware>>(Log);
            services.AddSingleton<IStartupFilter, TestPeer>();
            services.AddControllers().AddApplicationPart(typeof(HostingProbeController).Assembly);
        });
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions { BaseAddress = new Uri("https://stage.example.test"), AllowAutoRedirect = false });

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (disposing && Directory.Exists(contentRoot)) Directory.Delete(contentRoot, recursive: true);
    }

    private sealed class TestPeer : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use((context, next) =>
            {
                context.Connection.RemoteIpAddress = IPAddress.Parse(context.Request.Headers["X-Test-Peer"].FirstOrDefault() ?? "127.0.0.1");
                return next(context);
            });
            next(app);
        };
    }
}

// Registered only by StagingFactory, never shipped in the application.
public sealed class HostingProbeController : ControllerBase
{
    [HttpGet("/test/peer")]
    public object Peer() => new { ip = HttpContext.Connection.RemoteIpAddress?.ToString(), scheme = Request.Scheme, host = Request.Host.Value };
    [HttpGet("/test/error")]
    public IActionResult Error() => throw new InvalidOperationException("PRIVATE-EXCEPTION recipient@example.test token=SECRET");
}

[Collection("PostgreSQL")]
public sealed class HostingTests
{
    [Theory]
    [InlineData("AllowedHosts", "*", "AllowedHosts")]
    [InlineData("Email:AppUrl", "https://other.example.test/", "must match")]
    [InlineData("OpenIddict:Issuer", "https://stage.example.test/path", "HTTPS origin")]
    [InlineData("Application:CertificateFolder", "", "CertificateFolder")]
    [InlineData("Application:CertificateFolder", "relative-certificates", "persistent directory")]
    [InlineData("Hosting:TrustedProxies:0", "0.0.0.0", "individual proxy")]
    [InlineData("ConnectionStrings:DefaultConnection", "Host=localhost;Database=ais3;Include Error Detail=true", "detailed errors")]
    public void DeploymentRejectsUnsafeOrIncompleteConfiguration(string key, string value, string message)
    {
        using var keys = new HostingKeys();
        var settings = new Dictionary<string, string?>(keys.Settings) { [key] = value };
        using var factory = new StagingFactory(settings);
        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.Contains(message, error.Message);
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("password")]
    [InlineData("expired")]
    [InlineData("wrong-usage")]
    public void ExistingInvalidCertificatesAreNeverReplaced(string kind)
    {
        using var keys = new HostingKeys();
        var path = keys.Certificate("signing-certificate",
            kind == "wrong-usage" ? X509KeyUsageFlags.KeyEncipherment : X509KeyUsageFlags.DigitalSignature,
            days: kind == "expired" ? -1 : 10, password: kind == "password" ? "private-test-password" : null);
        if (kind == "corrupt") File.WriteAllText(path, "private-invalid-certificate");
        var original = File.ReadAllBytes(path);
        using var factory = new StagingFactory(keys.Settings);
        var error = Assert.Throws<InvalidOperationException>(() => factory.Browser());
        Assert.Contains("signing-certificate.pfx", error.Message);
        Assert.DoesNotContain("private-", error.ToString());
        Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void UnknownDeploymentEnvironmentIsRejected()
    {
        using var keys = new HostingKeys();
        using var factory = new StagingFactory(keys.Settings, "StageTypo");
        Assert.Contains("environment", Assert.Throws<InvalidOperationException>(() => factory.Browser()).Message);
    }

    [Fact]
    public async Task ConcurrentFirstStartsCreateOnePersistentPasswordlessPair()
    {
        using var keys = new HostingKeys();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(keys.Settings).Build();
        var fingerprints = await Task.WhenAll(Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            using var certificates = new DeploymentCertificates(configuration);
            return (certificates.Signing.Thumbprint, certificates.Encryption.Thumbprint);
        })));
        Assert.All(fingerprints, pair => Assert.Equal(fingerprints[0], pair));
        Assert.NotEqual(fingerprints[0].Item1, fingerprints[0].Item2);
        var folder = keys.Settings["Application:CertificateFolder"]!;
        Assert.Equal(2, Directory.GetFiles(folder).Length);
        foreach (var filename in new[] { "signing-certificate.pfx", "encryption-certificate.pfx" })
        {
            var path = Path.Combine(folder, filename);
            using var certificate = X509CertificateLoader.LoadPkcs12FromFile(path, null);
            Assert.True(certificate.HasPrivateKey);
            Assert.Equal(9999, certificate.NotAfter.Year);
            if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        }
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(folder));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void AnExistingCertificateIsReusedWhileTheMissingOneIsGenerated(bool signingExists)
    {
        using var keys = new HostingKeys();
        var path = keys.Certificate(signingExists ? "signing-certificate" : "encryption-certificate",
            signingExists ? X509KeyUsageFlags.DigitalSignature : X509KeyUsageFlags.KeyEncipherment);
        var original = File.ReadAllBytes(path);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(keys.Settings).Build();
        using var certificates = new DeploymentCertificates(configuration);
        Assert.Equal(original, File.ReadAllBytes(path));
        Assert.Equal(9999, (signingExists ? certificates.Encryption : certificates.Signing).NotAfter.Year);
    }

    [Fact]
    public void AnUnusableCertificateFolderFailsStartup()
    {
        using var keys = new HostingKeys();
        var path = keys.Settings["Application:CertificateFolder"]!;
        File.WriteAllText(path, "not a directory");
        using var factory = new StagingFactory(keys.Settings);
        Assert.Contains("CertificateFolder", Assert.Throws<InvalidOperationException>(() => factory.Browser()).Message);
    }

    [Fact]
    public void AnUnusableKeysSubfolderFailsStartup()
    {
        using var keys = new HostingKeys();
        Directory.CreateDirectory(keys.Settings["Application:CertificateFolder"]!);
        File.WriteAllText(keys.KeyRingDirectory, "not a directory");
        using var factory = new StagingFactory(keys.Settings);
        Assert.Contains("keys subfolder", Assert.Throws<InvalidOperationException>(() => factory.Browser()).Message);
        Assert.Equal("not a directory", File.ReadAllText(keys.KeyRingDirectory));
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task DeploymentPreservesAccountLayoutAndAddsHeadersWithoutExposingErrors(string environment)
    {
        using var keys = new HostingKeys();
        using var factory = new StagingFactory(keys.Settings, environment);
        using var browser = factory.Browser();
        var response = await browser.GetAsync("/Account/Login?private=PRIVATE-QUERY");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("One account.", await response.Content.ReadAsStringAsync());
        Assert.Contains("script-src 'self'", response.Headers.GetValues("Content-Security-Policy").Single());
        Assert.Equal("DENY", response.Headers.GetValues("X-Frame-Options").Single());
        Assert.Equal("nosniff", response.Headers.GetValues("X-Content-Type-Options").Single());
        Assert.Equal("no-referrer", response.Headers.GetValues("Referrer-Policy").Single());
        Assert.Contains("max-age=", response.Headers.GetValues("Strict-Transport-Security").Single());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/admin/")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/admin/session")).StatusCode);
        Assert.Empty(factory.Log.Lines);
        var error = await browser.GetAsync("/test/error?token=PRIVATE-QUERY");
        Assert.Equal(HttpStatusCode.InternalServerError, error.StatusCode);
        Assert.Equal("The request could not be completed. Please try again.", await error.Content.ReadAsStringAsync());
        Assert.True(error.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.NotFound, (await browser.GetAsync("/unmatched/PRIVATE-PATH")).StatusCode);
        var failure = Assert.Single(factory.Log.Lines);
        Assert.Contains(nameof(InvalidOperationException), failure);
        Assert.Contains("test/error", failure);
        Assert.DoesNotContain("PRIVATE", failure);
        Assert.DoesNotContain("recipient@example.test", failure);
        var discovery = await browser.GetStringAsync("/.well-known/openid-configuration");
        Assert.Contains("https://stage.example.test/", discovery);
        var keyXml = string.Join('\n', Directory.GetFiles(keys.KeyRingDirectory, "*.xml").Select(File.ReadAllText));
        Assert.Contains("encryptedSecret", keyXml);
        Assert.DoesNotContain("<masterKey", keyXml);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(keys.KeyRingDirectory));
    }

    [Theory]
    [InlineData("Staging", "127.0.0.1")]
    [InlineData("Staging", "::1")]
    [InlineData("Production", "127.0.0.1")]
    [InlineData("Production", "::1")]
    public async Task DefaultProxyTrustIsLoopbackOnlyAndOneHopWithoutForwardingHost(string environment, string peer)
    {
        using var keys = new HostingKeys();
        using var factory = new StagingFactory(keys.Settings, environment);
        using var browser = factory.Browser();
        using var trusted = new HttpRequestMessage(HttpMethod.Get, "http://stage.example.test/test/peer");
        trusted.Headers.Add("X-Test-Peer", peer);
        trusted.Headers.Add("X-Forwarded-Proto", "https");
        trusted.Headers.Add("X-Forwarded-For", "192.0.2.1, 198.51.100.7");
        trusted.Headers.Add("X-Forwarded-Host", "evil.example.test");
        var response = await browser.SendAsync(trusted);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("{\"ip\":\"198.51.100.7\",\"scheme\":\"https\",\"host\":\"stage.example.test\"}", await response.Content.ReadAsStringAsync());
        using var untrusted = new HttpRequestMessage(HttpMethod.Get, "http://stage.example.test/Account/Login");
        untrusted.Headers.Add("X-Test-Peer", "203.0.113.8");
        untrusted.Headers.Add("X-Forwarded-Proto", "https");
        untrusted.Headers.Add("X-Forwarded-For", "198.51.100.7");
        var redirect = await browser.SendAsync(untrusted);
        Assert.Equal(HttpStatusCode.TemporaryRedirect, redirect.StatusCode);
        Assert.Equal("https://stage.example.test/Account/Login", redirect.Headers.Location!.AbsoluteUri);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync("https://evil.example.test/Account/Login")).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("http://stage.example.test/health")).StatusCode);
    }

    [Theory]
    [InlineData("Staging")]
    [InlineData("Production")]
    public async Task ExplicitProxyAddressesReplaceTheLoopbackDefaults(string environment)
    {
        using var keys = new HostingKeys();
        keys.Settings["Hosting:TrustedProxies:0"] = "203.0.113.8";
        using var factory = new StagingFactory(keys.Settings, environment);
        using var browser = factory.Browser();
        foreach (var peer in new[] { "203.0.113.8", "127.0.0.1", "::1", "203.0.113.9" })
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, "http://stage.example.test/test/peer");
            request.Headers.Add("X-Test-Peer", peer);
            request.Headers.Add("X-Forwarded-Proto", "https");
            request.Headers.Add("X-Forwarded-For", "198.51.100.7");
            using var response = await browser.SendAsync(request);
            if (peer == "203.0.113.8")
            {
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Equal("{\"ip\":\"198.51.100.7\",\"scheme\":\"https\",\"host\":\"stage.example.test\"}", await response.Content.ReadAsStringAsync());
            }
            else Assert.Equal(HttpStatusCode.TemporaryRedirect, response.StatusCode);
        }
    }

    [PostgresFact]
    public async Task CookiesAccountTokensAndOidcGrantsSurviveRestartWithGeneratedCertificates()
    {
        using var keys = new HostingKeys();
        await using var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        const string email = "stage@example.test", password = "Stage-test-Password-42!";
        await database.Services.GetRequiredService<BootstrapService>().RunAsync(new(email, "Stage", "Test"), () => Task.FromResult(password));
        await database.Services.GetRequiredService<IOpenIddictApplicationManager>().CreateAsync(new OpenIddictApplicationDescriptor
        {
            ClientId = "stage-client",
            ClientType = ClientTypes.Public,
            ConsentType = ConsentTypes.Implicit,
            RedirectUris = { new Uri("https://client.example.test/callback") },
            Permissions = { Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken, Permissions.ResponseTypes.Code, Permissions.Scopes.Profile },
            Requirements = { Requirements.Features.ProofKeyForCodeExchange }
        });
        keys.Settings["ConnectionStrings:DefaultConnection"] = database.Db.Database.GetConnectionString();
        var verifier = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
        var authorize = QueryHelpers.AddQueryString("/connect/authorize", new Dictionary<string, string?>
        {
            ["client_id"] = "stage-client",
            ["redirect_uri"] = "https://client.example.test/callback",
            ["response_type"] = "code",
            ["scope"] = "openid profile offline_access",
            ["code_challenge_method"] = "S256",
            ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
        });
        string session, reset, confirmation, code, refresh, access;
        async Task<HttpResponseMessage> Redeem(HttpClient browser, string value, bool renewal = false) => await browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = "stage-client",
            ["grant_type"] = renewal ? "refresh_token" : "authorization_code",
            [renewal ? "refresh_token" : "code"] = value,
            ["redirect_uri"] = "https://client.example.test/callback",
            ["code_verifier"] = verifier
        }));
        async Task<string> Code(HttpClient browser)
        {
            var response = await browser.GetAsync(authorize);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            return QueryHelpers.ParseQuery(response.Headers.Location!.Query)["code"].ToString();
        }
        using (var first = new StagingFactory(keys.Settings))
        using (var browser = first.Browser())
        {
            var html = await browser.GetStringAsync("/Account/Login");
            var login = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["Email"] = email,
                ["Password"] = password,
                ["__RequestVerificationToken"] = OidcTestClient.Field(html, "__RequestVerificationToken")
            }));
            Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
            session = AuthenticationFactory.SessionCookie(login).Split(';')[0];
            using var scope = first.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            reset = await users.GeneratePasswordResetTokenAsync((await users.FindByEmailAsync(email))!);
            confirmation = await users.GenerateEmailConfirmationTokenAsync((await users.FindByEmailAsync(email))!);
            var tokens = await Redeem(browser, await Code(browser));
            Assert.Equal(HttpStatusCode.OK, tokens.StatusCode);
            using var json = JsonDocument.Parse(await tokens.Content.ReadAsStringAsync());
            refresh = json.RootElement.GetProperty("refresh_token").GetString()!;
            access = json.RootElement.GetProperty("access_token").GetString()!;
            code = await Code(browser);
            // The protocol's form_post mode must remain usable under CSP.
            var form = await browser.GetAsync(authorize + "&response_mode=form_post");
            Assert.Equal(HttpStatusCode.OK, form.StatusCode);
            Assert.Contains("<form", await form.Content.ReadAsStringAsync());
            Assert.DoesNotContain("form-action", form.Headers.GetValues("Content-Security-Policy").Single());
        }
        var folder = keys.Settings["Application:CertificateFolder"]!;
        var originalFiles = Directory.GetFiles(folder).ToDictionary(path => path, File.ReadAllBytes);
        var originalKeyFiles = Directory.GetFiles(keys.KeyRingDirectory, "*.xml").ToDictionary(path => path, File.ReadAllBytes);
        Assert.NotEmpty(originalKeyFiles);
        using (var second = new StagingFactory(keys.Settings))
        using (var browser = second.Browser())
        {
            browser.DefaultRequestHeaders.Add("Cookie", session);
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account")).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/admin/overview")).StatusCode);
            using var scope = second.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.True(await users.VerifyUserTokenAsync((await users.FindByEmailAsync(email))!, "AIS3.PasswordReset", "ResetPassword", reset));
            Assert.True(await users.VerifyUserTokenAsync((await users.FindByEmailAsync(email))!, "AIS3.EmailConfirmation", "EmailConfirmation", confirmation));
            Assert.Equal(HttpStatusCode.OK, (await Redeem(browser, code)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await Redeem(browser, refresh, renewal: true)).StatusCode);
            using var info = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
            info.Headers.Authorization = new("Bearer", access);
            Assert.Equal(HttpStatusCode.OK, (await browser.SendAsync(info)).StatusCode);
        }
        foreach (var (path, original) in originalFiles) Assert.Equal(original, File.ReadAllBytes(path));
        foreach (var (path, original) in originalKeyFiles) Assert.Equal(original, File.ReadAllBytes(path));
    }

    [Fact]
    public void LosingTheEncryptionCertificateDoesNotSilentlyResetTheKeyRing()
    {
        using var keys = new HostingKeys();
        using (var first = new StagingFactory(keys.Settings))
        using (var browser = first.Browser()) { }
        var keyFiles = Directory.GetFiles(keys.KeyRingDirectory, "*.xml").ToDictionary(path => path, File.ReadAllBytes);
        File.Delete(Path.Combine(keys.Settings["Application:CertificateFolder"]!, "encryption-certificate.pfx"));
        using var broken = new StagingFactory(keys.Settings);
        Assert.Contains("key ring", Assert.Throws<InvalidOperationException>(() => broken.Browser()).Message);
        foreach (var (path, original) in keyFiles) Assert.Equal(original, File.ReadAllBytes(path));
    }
}
