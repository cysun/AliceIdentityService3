using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Tests;

[Collection("PostgreSQL")]
public sealed class AdminUserOperationTests
{
    private const string NewPassword = "Changed-Password-74!";
    private static async Task<User> AddUser(OidcTestClient client, bool admin = false)
    {
        await using var scope = client.Factory.Services.CreateAsyncScope();
        var manager = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User { Email = Guid.NewGuid() + "@example.test", FirstName = "Other", LastName = "Person", ScreenName = "Other Person", EmailConfirmed = true };
        user.UserName = user.Email;
        Assert.True((await manager.CreateAsync(user, OidcTestClient.Password)).Succeeded);
        if (admin) Assert.True((await manager.AddClaimAsync(user, new Claim(AisConstants.AdminClaim, "false"))).Succeeded);
        return user;
    }

    private static async Task Login(HttpClient browser, string email = OidcTestClient.Email, string password = OidcTestClient.Password)
    {
        var html = await browser.GetStringAsync("/Account/Login");
        var response = await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = password,
            ["__RequestVerificationToken"] = OidcTestClient.Field(html, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    private static async Task<string> Csrf(HttpClient browser)
    {
        var response = await browser.GetAsync("/api/admin/antiforgery");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(string.IsNullOrEmpty(body.GetProperty("userId").GetString()));
        return body.GetProperty("requestToken").GetString()!;
    }

    private static async Task<HttpResponseMessage> Write(HttpClient browser, string path, object input, HttpMethod? method = null, string? token = null)
    {
        using var request = new HttpRequestMessage(method ?? HttpMethod.Post, "/api/admin/users" + path) { Content = JsonContent.Create(input) };
        if (token != "missing") request.Headers.Add("X-CSRF-TOKEN", token ?? await Csrf(browser));
        return await browser.SendAsync(request);
    }

    private static async Task<User> Read(OidcTestClient client, string id) =>
        await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Id == id);
    private static string Version(User user) => AdminUserVersion.For(user.Id, user.ConcurrencyStamp);
    private static object Profile(User user, bool confirmed = true) => new
    {
        version = Version(user),
        firstName = " Revised ",
        lastName = " Person ",
        screenName = " New Screen ",
        emailConfirmed = confirmed,
        email = "injected@example.test",
        userName = "injected",
        securityStamp = "injected",
        passwordHash = "injected",
        claims = new[] { "ais_admin" }
    };
    private static async Task Ok(HttpResponseMessage response) => Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());

    [PostgresFact]
    public async Task EveryWriteRequiresCurrentAdministratorAndValidUserBoundAntiforgery()
    {
        await using var client = await OidcTestClient.Create();
        var target = await AddUser(client);
        using var ordinary = client.Factory.Browser();
        await Login(ordinary, target.Email!);
        foreach (var browser in new[] { client.Browser, ordinary })
        {
            var expected = browser == ordinary ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            foreach (var path in new[] { "", "/" + target.Id + "/profile", "/" + target.Id + "/password", "/" + target.Id + "/lock", "/" + target.Id + "/unlock", "/" + target.Id })
                Assert.Equal(expected, (await Write(browser, path, new { }, path.EndsWith(target.Id) ? HttpMethod.Delete : null, "missing")).StatusCode);
        }
        await Login(client.Browser);
        foreach (var path in new[] { "", "/" + target.Id + "/profile", "/" + target.Id + "/password", "/" + target.Id + "/lock", "/" + target.Id + "/unlock", "/" + target.Id })
        {
            var method = path.EndsWith(target.Id) ? HttpMethod.Delete : HttpMethod.Post;
            foreach (var token in new[] { "missing", "forged" })
                Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, path, new { }, method, token)).StatusCode);
        }
        var second = await AddUser(client, admin: true);
        using var secondBrowser = client.Factory.Browser();
        await Login(secondBrowser, second.Email!);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + target.Id + "/profile", Profile(target), token: await Csrf(secondBrowser))).StatusCode);
        foreach (var operation in new[] { "profile", "password", "lock", "unlock" })
            Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.Browser.GetAsync($"/api/admin/users/{target.Id}/{operation}")).StatusCode);
        Assert.Equal(target.SecurityStamp, (await Read(client, target.Id)).SecurityStamp);
    }

    [PostgresFact]
    public async Task CreateValidatesNamesPasswordsAndDuplicatesWithoutAssigningClaims()
    {
        await using var client = await OidcTestClient.Create();
        await Login(client.Browser);
        object Input(string email, string password = NewPassword, string firstName = " Created ", string? confirm = null) => new
        { firstName, lastName = " User ", screenName = " Created User ", email, password, confirmPassword = confirm ?? password, emailConfirmed = false, claims = new[] { "ais_admin" } };
        foreach (var input in new[] { Input("invalid"), Input("new@example.test", "weak"), Input("new@example.test", firstName: " "), Input("new@example.test", confirm: "different") })
            Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "", input)).StatusCode);
        var response = await Write(client.Browser, "", Input("created@example.test"));
        await Ok(response);
        var result = (await response.Content.ReadFromJsonAsync<AdminMutationResult>())!;
        Assert.False(result.SessionEnded);
        var user = await Read(client, result.Id);
        Assert.Equal("Created", user.FirstName);
        Assert.Equal("User", user.LastName);
        Assert.Equal("Created User", user.ScreenName);
        Assert.True(user.EmailConfirmed);
        Assert.Equal(user.Email, user.UserName);
        Assert.Empty(await client.Database.Db.UserClaims.Where(claim => claim.UserId == user.Id).ToListAsync());
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "", Input("CREATED@EXAMPLE.TEST"))).StatusCode);
        using var createdBrowser = client.Factory.Browser();
        await Login(createdBrowser, user.Email!, NewPassword);
        Assert.Equal(HttpStatusCode.Forbidden, (await createdBrowser.GetAsync("/api/admin/session")).StatusCode);
    }

    [PostgresFact]
    public async Task ProfileWhitelistsFieldsUpdatesSearchAndRejectsStaleOrMissingEdits()
    {
        await using var client = await OidcTestClient.Create();
        await Login(client.Browser);
        var target = await AddUser(client);
        var detail = await client.Browser.GetFromJsonAsync<AdminUserDetail>("/api/admin/users/" + target.Id);
        Assert.Equal(Version(target), detail!.Version);
        await Ok(await Write(client.Browser, "/" + target.Id + "/profile", Profile(target)));
        var edited = await Read(client, target.Id);
        Assert.Equal("Revised", edited.FirstName);
        Assert.Equal("New Screen", edited.ScreenName);
        Assert.Equal(target.Email, edited.Email);
        Assert.Equal(target.UserName, edited.UserName);
        Assert.Equal(target.PasswordHash, edited.PasswordHash);
        Assert.Equal(target.SecurityStamp, edited.SecurityStamp);
        Assert.NotEqual(Version(target), Version(edited));
        Assert.Empty(await client.Database.Db.UserClaims.Where(claim => claim.UserId == target.Id).ToListAsync());
        var found = await client.Browser.GetFromJsonAsync<AdminUserPage>("/api/admin/users?q=revised+screen");
        Assert.Equal(target.Id, Assert.Single(found!.Items).Id);
        Assert.Equal(HttpStatusCode.Conflict, (await Write(client.Browser, "/" + target.Id + "/profile", Profile(target))).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Write(client.Browser, "/missing/profile", Profile(edited))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + target.Id + "/profile", new { version = Version(edited), firstName = "x", lastName = "y", screenName = "z" })).StatusCode);
        Assert.Equal(Version(edited), Version(await Read(client, target.Id)));
    }

    [PostgresFact]
    public async Task SecurityOperationsRevokeCookiesCodesRefreshTokensAndOutstandingResetLinks()
    {
        foreach (var operation in new[] { "password", "profile", "lock", "unlock", "delete" })
        {
            await using var client = await OidcTestClient.Create();
            var issued = await client.Tokens();
            var code = await client.Code();
            var target = await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Email == OidcTestClient.Email);
            string resetToken;
            await using (var scope = client.Factory.Services.CreateAsyncScope())
                resetToken = await scope.ServiceProvider.GetRequiredService<UserManager<User>>().GeneratePasswordResetTokenAsync(target);
            var actor = await AddUser(client, admin: true);
            using var admin = client.Factory.Browser();
            await Login(admin, actor.Email!);
            object input = operation switch
            {
                "password" => new { version = Version(target), password = NewPassword, confirmPassword = NewPassword },
                "profile" => Profile(target, confirmed: false),
                "lock" => new { version = Version(target), duration = "indefinite" },
                "delete" => new { version = Version(target), confirmation = target.Email },
                _ => new { version = Version(target) }
            };
            await Ok(await Write(admin, "/" + target.Id + (operation == "delete" ? "" : "/" + operation), input, operation == "delete" ? HttpMethod.Delete : null));
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.Browser.GetAsync("/api/admin/session")).StatusCode);
            Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Redeem(code)));
            Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Refresh(issued.GetProperty("refresh_token").GetString()!)));
            Assert.All(await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().Where(token => token.Subject == target.Id).ToListAsync(), token => Assert.Equal(Statuses.Revoked, token.Status));
            Assert.All(await client.Database.Db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking().Where(auth => auth.Subject == target.Id).ToListAsync(), auth => Assert.Equal(Statuses.Revoked, auth.Status));
            Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/session")).StatusCode);
            if (operation == "delete")
            {
                Assert.False(await client.Database.Db.Users.AnyAsync(user => user.Id == target.Id));
                Assert.False(await client.Database.Db.UserClaims.AnyAsync(claim => claim.UserId == target.Id));
                continue;
            }
            var changed = await Read(client, target.Id);
            Assert.NotEqual(target.SecurityStamp, changed.SecurityStamp);
            await using var check = client.Factory.Services.CreateAsyncScope();
            var manager = check.ServiceProvider.GetRequiredService<UserManager<User>>();
            Assert.False(await manager.VerifyUserTokenAsync(changed, manager.Options.Tokens.PasswordResetTokenProvider, "ResetPassword", resetToken));
            if (operation == "password")
            {
                Assert.True(await manager.CheckPasswordAsync(changed, NewPassword));
                Assert.False(await manager.CheckPasswordAsync(changed, OidcTestClient.Password));
            }
        }
    }

    [PostgresFact]
    public async Task LockDurationsUnlockAndPasswordValidationPreserveOtherSecuritySettings()
    {
        await using var client = await OidcTestClient.Create();
        await Login(client.Browser);
        var target = await AddUser(client);
        foreach (var duration in new[] { "hour", "day", "indefinite" })
        {
            await Ok(await Write(client.Browser, "/" + target.Id + "/lock", new { version = Version(target), duration }));
            target = await Read(client, target.Id);
            Assert.True(target.LockoutEnabled);
            Assert.Equal(duration == "hour" ? client.Factory.Clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds() : duration == "day" ? client.Factory.Clock.GetUtcNow().AddDays(1).ToUnixTimeSeconds() : DateTimeOffset.MaxValue.ToUnixTimeSeconds(), target.LockoutEnd!.Value.ToUnixTimeSeconds());
        }
        await client.Database.Db.Users.Where(user => user.Id == target.Id).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.TwoFactorEnabled, true).SetProperty(user => user.AccessFailedCount, 4));
        foreach (var password in new[] { "weak", NewPassword })
        {
            var result = await Write(client.Browser, "/" + target.Id + "/password", new { version = Version(target), password, confirmPassword = password });
            Assert.Equal(password == "weak" ? HttpStatusCode.BadRequest : HttpStatusCode.OK, result.StatusCode);
            var changed = await Read(client, target.Id);
            Assert.True(changed.TwoFactorEnabled);
            Assert.Equal(target.LockoutEnd, changed.LockoutEnd);
            if (password == "weak") Assert.Equal(target.SecurityStamp, changed.SecurityStamp);
            target = changed;
        }
        await Ok(await Write(client.Browser, "/" + target.Id + "/unlock", new { version = Version(target) }));
        var unlocked = await Read(client, target.Id);
        Assert.Null(unlocked.LockoutEnd);
        Assert.Equal(0, unlocked.AccessFailedCount);
        Assert.True(unlocked.TwoFactorEnabled);
        Assert.True(unlocked.LockoutEnabled);
        Assert.NotEqual(target.SecurityStamp, unlocked.SecurityStamp);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + target.Id + "/lock", new { version = Version(unlocked), duration = "forever" })).StatusCode);
    }

    [PostgresFact]
    public async Task SelfProtectionAndOwnPasswordResetHaveExplicitSessionBehavior()
    {
        await using var client = await OidcTestClient.Create();
        await Login(client.Browser);
        var self = await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Email == OidcTestClient.Email);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + self.Id + "/lock", new { version = Version(self), duration = "day" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + self.Id + "/profile", Profile(self, false))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + self.Id, new { version = Version(self), confirmation = self.Email }, HttpMethod.Delete)).StatusCode);
        Assert.Equal(Version(self), Version(await Read(client, self.Id)));
        var reset = await Write(client.Browser, "/" + self.Id + "/password", new { version = Version(self), password = NewPassword, confirmPassword = NewPassword });
        await Ok(reset);
        Assert.True((await reset.Content.ReadFromJsonAsync<AdminMutationResult>())!.SessionEnded);
        Assert.Contains(reset.Headers.GetValues("Set-Cookie"), value => value.StartsWith("__Host-AIS3.Session=;", StringComparison.Ordinal));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.Browser.GetAsync("/api/admin/session")).StatusCode);
        await Login(client.Browser, password: NewPassword);
    }

    [PostgresFact]
    public async Task DeleteRequiresExactConfirmationAndSupportsLegacyAccountsWithoutEmail()
    {
        await using var client = await OidcTestClient.Create();
        await Login(client.Browser);
        var target = await AddUser(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + target.Id, new { version = Version(target), confirmation = "wrong" }, HttpMethod.Delete)).StatusCode);
        await client.Database.Db.Users.Where(user => user.Id == target.Id).ExecuteUpdateAsync(setters => setters.SetProperty(user => user.Email, "").SetProperty(user => user.EmailConfirmed, false));
        target = await Read(client, target.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + target.Id + "/profile", Profile(target))).StatusCode);
        await Ok(await Write(client.Browser, "/" + target.Id, new { version = Version(target), confirmation = target.Id }, HttpMethod.Delete));
        Assert.False(await client.Database.Db.Users.AnyAsync(user => user.Id == target.Id));
    }

    [PostgresFact]
    public async Task IdentityAndTokenChangesRollBackWhenAuthorizationRevocationFails()
    {
        await using var client = await OidcTestClient.Create();
        await client.Tokens();
        var target = await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Email == OidcTestClient.Email);
        var actor = await AddUser(client, admin: true);
        using var admin = client.Factory.Browser();
        await Login(admin, actor.Email!);
        var previousTokens = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().OrderBy(token => token.Id).Select(token => new { token.Id, token.Status }).ToListAsync();
        await client.Database.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_revoke() RETURNS trigger LANGUAGE plpgsql AS $$ BEGIN RAISE EXCEPTION 'Test revocation failure'; END $$;
            CREATE TRIGGER fail_revoke BEFORE UPDATE ON "OpenIddictAuthorizations" FOR EACH ROW EXECUTE FUNCTION fail_revoke();
            """);
        foreach (var operation in new[] { "password", "lock", "delete" })
        {
            object input = operation switch
            {
                "password" => new { version = Version(target), password = NewPassword, confirmPassword = NewPassword },
                "lock" => new { version = Version(target), duration = "day" },
                _ => new { version = Version(target), confirmation = target.Email }
            };
            // TestServer propagates an unhandled server exception instead of generating Kestrel's 500.
            var failure = await Assert.ThrowsAsync<PostgresException>(() => Write(admin, "/" + target.Id + (operation == "delete" ? "" : "/" + operation), input, operation == "delete" ? HttpMethod.Delete : null));
            Assert.Equal("P0001", failure.SqlState);
            var unchanged = await Read(client, target.Id);
            Assert.Equal(target.PasswordHash, unchanged.PasswordHash);
            Assert.Equal(target.SecurityStamp, unchanged.SecurityStamp);
            Assert.Equal(target.ConcurrencyStamp, unchanged.ConcurrencyStamp);
            Assert.Null(unchanged.LockoutEnd);
            Assert.True(await client.Database.Db.UserClaims.AnyAsync(claim => claim.UserId == target.Id));
            Assert.Equal(previousTokens, await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().OrderBy(token => token.Id).Select(token => new { token.Id, token.Status }).ToListAsync());
        }
    }

    [PostgresFact]
    public async Task ConcurrentAdministratorsCannotLockEachOtherAndStaleWritesCannotOverwrite()
    {
        await using var client = await OidcTestClient.Create();
        var first = await AddUser(client, admin: true);
        var second = await AddUser(client, admin: true);
        async Task<AdminMutationOutcome> Lock(User actor, User target)
        {
            await using var scope = client.Factory.Services.CreateAsyncScope();
            var principal = await scope.ServiceProvider.GetRequiredService<SignInManager<User>>().CreateUserPrincipalAsync(actor);
            return await scope.ServiceProvider.GetRequiredService<AdminUserMutationService>().LockAsync(principal, target.Id, new() { Version = Version(target), Duration = "day" }, CancellationToken.None);
        }
        var results = await Task.WhenAll(Lock(first, second), Lock(second, first));
        Assert.Single(results, result => result.Status == 200);
        Assert.Single(results, result => result.Status == 401);
        await Login(client.Browser);
        var target = await AddUser(client);
        var csrf = await Csrf(client.Browser);
        var edits = await Task.WhenAll(Write(client.Browser, "/" + target.Id + "/profile", Profile(target), token: csrf), Write(client.Browser, "/" + target.Id + "/profile", Profile(target), token: csrf));
        Assert.Single(edits, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(edits, response => response.StatusCode == HttpStatusCode.Conflict);
    }
}
