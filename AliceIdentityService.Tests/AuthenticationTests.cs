using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.RegularExpressions;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AliceIdentityService.Tests;

internal sealed class TestClock : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;
    public override DateTimeOffset GetUtcNow() => now;
    public void Advance(TimeSpan amount) => now += amount;
}

internal sealed class AuthenticationFactory(string connection, Action<IServiceCollection>? configure = null) : PreviewFactory
{
    public TestClock Clock { get; } = new();
    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureAppConfiguration((_, configuration) => configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connection
        }));
        builder.ConfigureServices(services =>
        {
            services.AddDataProtection().UseEphemeralDataProtectionProvider();
            services.Configure<CookieAuthenticationOptions>(IdentityConstants.ApplicationScheme, options => options.TimeProvider = Clock);
            services.AddSingleton<TimeProvider>(Clock);
            configure?.Invoke(services);
        });
    }

    public HttpClient Browser() => CreateClient(new WebApplicationFactoryClientOptions
    {
        BaseAddress = new Uri("https://localhost"),
        AllowAutoRedirect = false
    });

    public AuthenticationTicket ReadTicket(HttpResponseMessage response)
    {
        var cookie = SessionCookie(response).Split(';')[0].Split('=', 2)[1];
        return Services.GetRequiredService<IOptionsMonitor<CookieAuthenticationOptions>>()
            .Get(IdentityConstants.ApplicationScheme).TicketDataFormat.Unprotect(cookie)!;
    }

    public static string SessionCookie(HttpResponseMessage response) => response.Headers.GetValues("Set-Cookie")
        .Single(value => value.StartsWith("__Host-AIS3.Session=", StringComparison.Ordinal));
}

[Collection("PostgreSQL")]
public sealed class AuthenticationTests
{
    private const string Password = "Test-only-Strong-Password-42!";
    private const string Email = "login@example.test";

    private static async Task<User> CreateUser(PostgresTestDatabase database, bool admin = false, bool confirmed = true)
    {
        var users = database.Services.GetRequiredService<UserManager<User>>();
        var user = new User { UserName = Email, Email = Email, FirstName = "Test", LastName = "User", ScreenName = "Test User", EmailConfirmed = confirmed };
        Assert.True((await users.CreateAsync(user, Password)).Succeeded);
        if (admin) Assert.True((await users.AddClaimAsync(user, new Claim(AisConstants.AdminClaim, "legacy-value"))).Succeeded);
        return user;
    }

    private static async Task<PostgresTestDatabase> Database()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        return database;
    }

    private static AuthenticationFactory Factory(PostgresTestDatabase database) => new(database.Db.Database.GetConnectionString()!);

    private static string Token(string html) => WebUtility.HtmlDecode(Regex.Match(html,
        "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"").Groups[1].Value);

    private static async Task<HttpResponseMessage> Login(HttpClient browser, string email = Email, string password = Password,
        bool remember = false, string? returnUrl = null)
    {
        var token = Token(await browser.GetStringAsync("/Account/Login"));
        Assert.NotEmpty(token);
        return await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Email"] = email,
            ["Password"] = password,
            ["RememberMe"] = remember.ToString(),
            ["ReturnUrl"] = returnUrl ?? ""
        }));
    }

    [PostgresFact]
    public async Task ConfirmedLoginAndPostLogoutUseAntiforgeryAndPreserveTheAccountLayout()
    {
        await using var database = await Database();
        await CreateUser(database);
        using var factory = Factory(database);
        using var browser = factory.Browser();
        var login = await Login(browser, email: Email.ToUpperInvariant());
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/Account", login.Headers.Location!.OriginalString);
        var home = await browser.GetAsync("/Account");
        var html = await home.Content.ReadAsStringAsync();
        Assert.Contains("One account.", html);
        Assert.Contains(Email, html);
        Assert.DoesNotContain("Open administration", html);
        Assert.True(home.Headers.CacheControl!.NoStore);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account/Logout")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/Account/Logout", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/Account")).StatusCode);
        var logout = await browser.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = Token(html),
            ["returnUrl"] = "https://evil.example/"
        }));
        Assert.Equal("/Account/Login", logout.Headers.Location!.OriginalString);
        Assert.Contains("expires=Thu, 01 Jan 1970", AuthenticationFactory.SessionCookie(logout));
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Account")).StatusCode);
    }

    [PostgresFact]
    public async Task RememberMeChangesPersistenceAndFixedExpiryWithoutSliding()
    {
        await using var database = await Database();
        await CreateUser(database);
        using var factory = Factory(database);
        foreach (var remember in new[] { false, true })
        {
            using var browser = factory.Browser();
            var response = await Login(browser, remember: remember);
            var cookie = AuthenticationFactory.SessionCookie(response);
            var ticket = factory.ReadTicket(response);
            Assert.Equal(remember, ticket.Properties.IsPersistent);
            Assert.Equal(remember, cookie.Contains("expires=", StringComparison.Ordinal));
            Assert.Contains("secure", cookie);
            Assert.Contains("httponly", cookie);
            Assert.Contains("samesite=lax", cookie);
            Assert.Contains("path=/", cookie);
            Assert.DoesNotContain("domain=", cookie);
            var lifetime = remember ? TimeSpan.FromDays(14) : TimeSpan.FromHours(8);
            Assert.Equal(lifetime, ticket.Properties.ExpiresUtc - ticket.Properties.IssuedUtc);
            Assert.NotNull(ticket.Principal.FindFirst("sub"));
            Assert.Equal(Email, ticket.Principal.Identity!.Name);
            factory.Clock.Advance(lifetime - TimeSpan.FromMinutes(1));
            var active = await browser.GetAsync("/Account");
            Assert.Equal(HttpStatusCode.OK, active.StatusCode);
            Assert.False(active.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(value => value.StartsWith("__Host-AIS3.Session=")));
            factory.Clock.Advance(TimeSpan.FromMinutes(2));
            Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Account")).StatusCode);
        }
    }

    [PostgresFact]
    public async Task WrongUnknownUnconfirmedLockedAndTwoFactorAccountsHaveTheSameFailure()
    {
        await using var database = await Database();
        var user = await CreateUser(database);
        using var factory = Factory(database);
        string? failure = null;
        foreach (var state in new[] { "wrong", "unknown", "unconfirmed", "locked", "two-factor" })
        {
            database.Db.ChangeTracker.Clear();
            user = await database.Db.Users.SingleAsync();
            user.EmailConfirmed = state != "unconfirmed";
            user.LockoutEnd = state == "locked" ? DateTimeOffset.UtcNow.AddMinutes(5) : null;
            user.TwoFactorEnabled = state == "two-factor";
            await database.Db.SaveChangesAsync();
            using var browser = factory.Browser();
            var response = await Login(browser, email: state == "unknown" ? "missing@example.test" : Email,
                password: state == "wrong" ? "Wrong-password-42!" : Password);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            var html = await response.Content.ReadAsStringAsync();
            var message = Regex.Match(html, "<li>(Unable to sign in[^<]+)</li>").Groups[1].Value;
            Assert.NotEmpty(message);
            failure ??= message;
            Assert.Equal(failure, message);
            Assert.DoesNotContain(Password, html);
            Assert.DoesNotContain("Wrong-password-42!", html);
            Assert.False(response.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(value => value.StartsWith("__Host-AIS3.Session=")));
        }
    }

    [PostgresFact]
    public async Task FiveFailuresLockTheAccountAndSuccessAfterLockoutResetsFailures()
    {
        await using var database = await Database();
        await CreateUser(database);
        using var factory = Factory(database);
        using var browser = factory.Browser();
        for (var i = 0; i < 5; i++) Assert.Equal(HttpStatusCode.OK, (await Login(browser, password: "Wrong-42!")).StatusCode);
        database.Db.ChangeTracker.Clear();
        var user = await database.Db.Users.SingleAsync();
        Assert.InRange(user.LockoutEnd!.Value - DateTimeOffset.UtcNow, TimeSpan.FromMinutes(4), TimeSpan.FromMinutes(5));
        Assert.Equal(HttpStatusCode.OK, (await Login(browser)).StatusCode);
        user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(-1);
        await database.Db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Redirect, (await Login(browser)).StatusCode);
        database.Db.ChangeTracker.Clear();
        Assert.Equal(0, (await database.Db.Users.SingleAsync()).AccessFailedCount);
    }

    [PostgresFact]
    public async Task ReturnUrlsAreLocalForBothFreshAndAlreadyAuthenticatedLogins()
    {
        await using var database = await Database();
        await CreateUser(database, admin: true);
        using var factory = Factory(database);
        foreach (var path in new[] { "https://evil.example/", "//evil.example/", "/\\evil.example/", "javascript:alert(1)", "/admin/users?filter=active" })
        {
            using var browser = factory.Browser();
            var expected = path.StartsWith("/admin/", StringComparison.Ordinal) ? path : "/Account";
            Assert.Equal(expected, (await Login(browser, returnUrl: path)).Headers.Location!.OriginalString);
            var existing = await browser.GetAsync("/Account/Login?returnUrl=" + Uri.EscapeDataString(path));
            Assert.Equal(expected, existing.Headers.Location!.OriginalString);
        }
    }

    [PostgresFact]
    public async Task AdminPolicyProtectsDeepLinksStaticFilesAndApiAndReloadsClaims()
    {
        await using var database = await Database();
        var user = await CreateUser(database);
        using var factory = Factory(database);
        using var browser = factory.Browser();
        Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/system/info")).StatusCode);
        await Login(browser);
        var denied = await browser.GetAsync("/api/system/info");
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
        Assert.Null(denied.Headers.Location);
        foreach (var path in new[] { "/admin", "/admin/users/alex", "/admin/index.html", "/admin/assets/example.js" })
        {
            var response = await browser.GetAsync(path);
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            Assert.Contains("/Account/AccessDenied", response.Headers.Location!.OriginalString);
        }
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/Account/AccessDenied")).StatusCode);
        var users = database.Services.GetRequiredService<UserManager<User>>();
        Assert.True((await users.AddClaimAsync(user, new Claim("ais-admin", "true"))).Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/system/info")).StatusCode);
        Assert.True((await users.AddClaimAsync(user, new Claim(AisConstants.AdminClaim, "legacy-value"))).Succeeded);
        Assert.Equal(ApplicationInfo.Current, await browser.GetFromJsonAsync<ApplicationInfo>("/api/system/info"));
        foreach (var path in new[] { "/admin", "/admin/", "/admin/users/alex", "/admin/index.html" })
        {
            var html = await browser.GetStringAsync(path);
            Assert.Contains("id=\"root\"", html);
            var asset = Regex.Match(html, "src=\"(/admin/assets/[^\"]+)\"").Groups[1].Value;
            Assert.NotEmpty(asset);
            Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(asset)).StatusCode);
        }
        Assert.True((await users.RemoveClaimAsync(user, new Claim(AisConstants.AdminClaim, "legacy-value"))).Succeeded);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/system/info")).StatusCode);
    }

    [PostgresFact]
    public async Task StampChangeLockoutUnconfirmationAndDeletionRevokeExistingCookies()
    {
        await using var database = await Database();
        await CreateUser(database, admin: true);
        using var factory = Factory(database);
        foreach (var state in new[] { "stamp", "lockout", "unconfirmed", "deleted" })
        {
            using var browser = factory.Browser();
            Assert.Equal(HttpStatusCode.Redirect, (await Login(browser)).StatusCode);
            database.Db.ChangeTracker.Clear();
            var user = await database.Db.Users.SingleAsync();
            switch (state)
            {
                case "stamp": user.SecurityStamp = Guid.NewGuid().ToString(); break;
                case "lockout": user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1); break;
                case "unconfirmed": user.EmailConfirmed = false; break;
                case "deleted": database.Db.Users.Remove(user); break;
            }
            await database.Db.SaveChangesAsync();
            Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync("/api/system/info")).StatusCode);
            if (state != "deleted")
            {
                user.LockoutEnd = null;
                user.EmailConfirmed = true;
                await database.Db.SaveChangesAsync();
            }
        }
    }

    [PostgresFact]
    public async Task InvalidInputsAndForgedAntiforgeryNeverIssueACookieOrEchoAPassword()
    {
        await using var database = await Database();
        await CreateUser(database);
        using var factory = Factory(database);
        using var browser = factory.Browser();
        var response = await Login(browser, email: "not-an-email");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("validation-summary-errors", await response.Content.ReadAsStringAsync());
        Assert.DoesNotContain(Password, await response.Content.ReadAsStringAsync());
        var invalid = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = Email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = "forged"
        }));
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        var token = Token(await browser.GetStringAsync("/Account/Login"));
        var preview = await browser.PostAsync("/Account/Login?preview=oidc&app=campus-portal", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = Email,
            ["Password"] = Password,
            ["__RequestVerificationToken"] = token
        }));
        Assert.Equal(HttpStatusCode.BadRequest, preview.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Account")).StatusCode);
    }
}
