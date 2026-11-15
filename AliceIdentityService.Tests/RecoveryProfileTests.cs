using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace AliceIdentityService.Tests;

[Collection("PostgreSQL")]
public sealed class RecoveryProfileTests
{
    private const string NewPassword = "Recovery-Test-Password-73!";

    private static Task<OidcTestClient> Client(CapturedAccountMail mail) =>
        OidcTestClient.Create(services => services.AddSingleton<IAccountEmailSender>(mail));

    private static async Task<HttpResponseMessage> Post(HttpClient browser, string action, Dictionary<string, string> input,
        string? getPath = null)
    {
        var html = await browser.GetStringAsync(getPath ?? "/Account/" + action);
        input["__RequestVerificationToken"] = OidcTestClient.Field(html, "__RequestVerificationToken");
        Assert.NotEmpty(input["__RequestVerificationToken"]);
        return await browser.PostAsync("/Account/" + action, new FormUrlEncodedContent(input));
    }

    private static Task<HttpResponseMessage> Login(HttpClient browser, string password = OidcTestClient.Password) =>
        Post(browser, "Login", new() { ["Email"] = OidcTestClient.Email, ["Password"] = password });

    private static Task<HttpResponseMessage> Request(HttpClient browser, string email = OidcTestClient.Email, string? context = null) =>
        Post(browser, "ForgotPassword", new() { ["Email"] = email, ["OidcContext"] = context ?? "" });

    private static Dictionary<string, string> ResetInput(string path, string password = NewPassword)
    {
        var fields = QueryHelpers.ParseQuery(new Uri("https://localhost" + path).Query)
            .ToDictionary(pair => pair.Key, pair => pair.Value.ToString(), StringComparer.OrdinalIgnoreCase);
        fields["Password"] = fields["ConfirmPassword"] = password;
        return fields;
    }

    private static Task<HttpResponseMessage> Reset(HttpClient browser, string path, string password = NewPassword) =>
        Post(browser, "ResetPassword", ResetInput(path, password), path);

    private static Dictionary<string, string> ChangeInput(string current = OidcTestClient.Password, string password = NewPassword) =>
        new() { ["CurrentPassword"] = current, ["Password"] = password, ["ConfirmPassword"] = password };

    private static async Task<User> Current(OidcTestClient client) =>
        await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Email == OidcTestClient.Email);

    [PostgresFact]
    public async Task RecoveryIsGenericForUnknownUnconfirmedAmbiguousAndUnavailableMailAndSharesRecipientLimits()
    {
        var mail = new CapturedAccountMail();
        await using var client = await Client(mail);
        var users = client.Database.Services.GetRequiredService<UserManager<User>>();
        foreach (var (name, email, confirmed) in new[]
        {
            ("unconfirmed@example.test", "unconfirmed@example.test", false),
            ("legacy-one@example.test", "shared@example.test", true),
            ("legacy-two@example.test", "shared@example.test", true)
        })
        {
            Assert.True((await users.CreateAsync(new User
            {
                UserName = name,
                Email = email,
                FirstName = "Legacy",
                LastName = "User",
                ScreenName = "Legacy User",
                EmailConfirmed = confirmed
            }, OidcTestClient.Password)).Succeeded);
        }
        foreach (var email in new[] { "missing@example.test", "unconfirmed@example.test", "shared@example.test" })
        {
            var response = await Request(client.Browser, email);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("If this address belongs to an eligible confirmed account", await response.Content.ReadAsStringAsync());
        }
        Assert.Empty(mail.Resets);
        mail.Fail = true;
        var failure = await Request(client.Browser);
        Assert.Equal(HttpStatusCode.OK, failure.StatusCode);
        Assert.Contains("If this address belongs to an eligible confirmed account", await failure.Content.ReadAsStringAsync());
        mail.Fail = false;
        await Request(client.Browser);
        Assert.Empty(mail.Resets);
        client.Factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await Request(client.Browser, " OIDC@EXAMPLE.TEST ");
        Assert.Equal(OidcTestClient.Email, Assert.Single(mail.Resets).Email);
        client.Factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await Post(client.Browser, "ResendConfirmation", new() { ["Email"] = OidcTestClient.Email });
        await Request(client.Browser);
        Assert.Single(mail.Resets); // Confirmation and recovery share the same recipient cooldown.
        Assert.Empty(mail.Messages);
    }

    [PostgresFact]
    public async Task ResetRotatesPasswordAndStampRejectsAllOlderLinksCookiesCodesAndRefreshTokens()
    {
        var mail = new CapturedAccountMail();
        await using var client = await Client(mail);
        var tokens = await client.Tokens();
        var code = await client.Code();
        using var secondSession = client.Factory.Browser();
        await Login(secondSession);
        using var recoveryBrowser = client.Factory.Browser();
        var before = await Current(client);
        await Request(recoveryBrowser);
        var firstLink = mail.Resets.Single().Path;
        client.Factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await Request(recoveryBrowser);
        var secondLink = mail.Resets.Last().Path;
        var opened = await recoveryBrowser.GetAsync(firstLink);
        Assert.True(opened.Headers.CacheControl!.NoStore);
        Assert.Equal("no-referrer", opened.Headers.GetValues("Referrer-Policy").Single());
        Assert.Equal(before.PasswordHash, (await Current(client)).PasswordHash);
        Assert.Equal(before.SecurityStamp, (await Current(client)).SecurityStamp);
        var reset = await Reset(recoveryBrowser, firstLink);
        Assert.Equal(HttpStatusCode.OK, reset.StatusCode);
        Assert.Contains("Password reset", await reset.Content.ReadAsStringAsync());
        Assert.Contains("expires=Thu, 01 Jan 1970", AuthenticationFactory.SessionCookie(reset));
        var after = await Current(client);
        Assert.NotEqual(before.SecurityStamp, after.SecurityStamp);
        Assert.NotEqual(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.EmailConfirmed, after.EmailConfirmed);
        foreach (var browser in new[] { client.Browser, secondSession, recoveryBrowser })
            Assert.Equal(HttpStatusCode.Redirect, (await browser.GetAsync("/Account")).StatusCode);
        foreach (var link in new[] { firstLink, secondLink })
        {
            Assert.Equal(HttpStatusCode.BadRequest, (await recoveryBrowser.GetAsync(link)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(recoveryBrowser, "ResetPassword", ResetInput(link), "/Account/ForgotPassword")).StatusCode);
        }
        Assert.Equal("invalid_grant", await OidcTestClient.Error(await client.Refresh(tokens.GetProperty("refresh_token").GetString()!)));
        Assert.Equal("invalid_grant", await OidcTestClient.Error(await client.Redeem(code)));
        Assert.Equal(HttpStatusCode.OK, (await Login(recoveryBrowser)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Login(recoveryBrowser, NewPassword)).StatusCode);
    }

    [PostgresFact]
    public async Task RecoveryAndResetRequireCsrfValidateInputsAndNeverRedisplayPasswords()
    {
        var mail = new CapturedAccountMail();
        await using var client = await Client(mail);
        foreach (var route in new[] { "ForgotPassword", "ResetPassword" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.PostAsync("/Account/" + route, new FormUrlEncodedContent([]))).StatusCode);
        await Request(client.Browser, "invalid-email");
        Assert.Empty(mail.Resets);
        Assert.Equal(HttpStatusCode.BadRequest, (await Request(client.Browser, context: "https://evil.example")).StatusCode);
        await Request(client.Browser);
        var link = mail.Resets.Single().Path;
        foreach (var (password, confirmation) in new[] { ("weak", "weak"), (NewPassword, "different"), ("", ""), (new string('P', 4097), "") })
        {
            var input = ResetInput(link, password); input["ConfirmPassword"] = confirmation;
            var result = await Post(client.Browser, "ResetPassword", input, link);
            Assert.Equal(HttpStatusCode.OK, result.StatusCode);
            var html = await result.Content.ReadAsStringAsync();
            Assert.Contains("validation-errors", html);
            Assert.DoesNotContain("value=\"" + NewPassword, html);
            Assert.DoesNotContain("value=\"weak", html);
            Assert.Equal(HttpStatusCode.OK, (await client.Browser.GetAsync(link)).StatusCode);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.GetAsync("/Account/ResetPassword")).StatusCode);
        var fields = ResetInput(link);
        foreach (var (key, value) in new[] { ("code", "!invalid!"), ("userId", "missing") })
        {
            var invalid = new Dictionary<string, string>(fields, StringComparer.OrdinalIgnoreCase) { [key] = value };
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client.Browser, "ResetPassword", invalid, "/Account/ForgotPassword")).StatusCode);
        }
        var options = client.Factory.Services.GetRequiredService<IOptions<PasswordResetTokenOptions>>().Value;
        Assert.Equal(TimeSpan.FromHours(1), options.TokenLifespan);
        options.TokenLifespan = TimeSpan.FromSeconds(-1);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.GetAsync(link)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client.Browser, "ResetPassword", fields, "/Account/ForgotPassword")).StatusCode);
        options.TokenLifespan = TimeSpan.FromHours(1);
        await using var scope = client.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByEmailAsync(OidcTestClient.Email))!;
        var confirmationToken = await users.GenerateEmailConfirmationTokenAsync(user);
        fields["code"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(confirmationToken));
        Assert.Equal(HttpStatusCode.BadRequest, (await Post(client.Browser, "ResetPassword", fields, "/Account/ForgotPassword")).StatusCode);
        Assert.True((await users.UpdateSecurityStampAsync(user)).Succeeded);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.GetAsync(link)).StatusCode);
    }

    [PostgresFact]
    public async Task RecoveryResumesValidatedOidcOnlyAfterNewPasswordSignIn()
    {
        var mail = new CapturedAccountMail();
        await using var client = await Client(mail);
        var login = await (await client.Authorize()).Content.ReadAsStringAsync();
        var context = OidcTestClient.Field(login, "OidcContext");
        await Request(client.Browser, context: context);
        var path = mail.Resets.Single().Path;
        var resetForm = await client.Browser.GetStringAsync(path);
        Assert.Contains("account-shell oidc", resetForm);
        var invalid = ResetInput(path); invalid["ConfirmPassword"] = "wrong";
        var validation = await (await Post(client.Browser, "ResetPassword", invalid, path)).Content.ReadAsStringAsync();
        Assert.Equal(context, OidcTestClient.Field(validation, "OidcContext"));
        Assert.Contains("account-shell oidc", validation);
        var result = await Reset(client.Browser, path);
        Assert.Contains("account-shell oidc", await result.Content.ReadAsStringAsync());
        var newLogin = await client.Browser.GetStringAsync("/Account/Login?context=" + Uri.EscapeDataString(context));
        var next = await client.SubmitLogin(newLogin, NewPassword);
        Assert.StartsWith("/connect/authorize", next.Headers.Location!.OriginalString);
        var authorize = await client.Browser.GetAsync(next.Headers.Location);
        Assert.StartsWith(OidcTestClient.Callback, authorize.Headers.Location!.OriginalString);
        var query = QueryHelpers.ParseQuery(authorize.Headers.Location.Query);
        Assert.Equal(client.State, query["state"]);
        Assert.Equal(HttpStatusCode.OK, (await client.Redeem(query["code"]!)).StatusCode);
    }

    [PostgresFact]
    public async Task ExpiredOidcContextStillAllowsResetWithoutRedirectingToIt()
    {
        var mail = new CapturedAccountMail();
        await using var client = await Client(mail);
        await Request(client.Browser);
        var expired = client.Factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("AIS3.OidcLoginContext.v1").ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(new ValidatedLoginContext(OidcTestClient.ClientId, "/connect/authorize")), DateTimeOffset.UtcNow.AddMinutes(-1));
        var path = QueryHelpers.AddQueryString(mail.Resets.Single().Path, "oidcContext", expired);
        var result = await Reset(client.Browser, path);
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var html = await result.Content.ReadAsStringAsync();
        Assert.Contains("application sign-in request has expired", html);
        Assert.DoesNotContain("/Account/Login?context=", html);
        Assert.Null(result.Headers.Location);
    }

    [PostgresFact]
    public async Task ConcurrentUsesOfOneResetLinkCanOnlyChangeThePasswordOnce()
    {
        var mail = new CapturedAccountMail();
        await using var client = await Client(mail);
        await Request(client.Browser);
        var link = mail.Resets.Single().Path;
        using var second = client.Factory.Browser();
        var firstInput = ResetInput(link);
        var secondInput = ResetInput(link, "Another-Password-82!");
        firstInput["__RequestVerificationToken"] = OidcTestClient.Field(await client.Browser.GetStringAsync(link), "__RequestVerificationToken");
        secondInput["__RequestVerificationToken"] = OidcTestClient.Field(await second.GetStringAsync(link), "__RequestVerificationToken");
        var responses = await Task.WhenAll(
            client.Browser.PostAsync("/Account/ResetPassword", new FormUrlEncodedContent(firstInput)),
            second.PostAsync("/Account/ResetPassword", new FormUrlEncodedContent(secondInput)));
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.OK);
        Assert.Single(responses, response => response.StatusCode == HttpStatusCode.BadRequest);
        Assert.Equal(HttpStatusCode.Redirect, (await Login(client.Browser,
            responses[0].IsSuccessStatusCode ? NewPassword : "Another-Password-82!")).StatusCode);
    }

    [PostgresFact]
    public async Task PasswordChangeRequiresCurrentPasswordAndRevokesOtherSessionsAndRefreshTokens()
    {
        var mail = new CapturedAccountMail();
        await using var client = await Client(mail);
        var tokens = await client.Tokens();
        using var second = client.Factory.Browser();
        await Login(second);
        await Request(client.Browser);
        var oldLink = mail.Resets.Single().Path;
        var before = await Current(client);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.PostAsync("/Account/ChangePassword", new FormUrlEncodedContent(ChangeInput()))).StatusCode);
        foreach (var input in new[] { ChangeInput(current: "wrong"), ChangeInput(password: "weak"), ChangeInput() })
        {
            if (input["CurrentPassword"] == OidcTestClient.Password && input["Password"] == NewPassword) input["ConfirmPassword"] = "mismatch";
            var result = await Post(client.Browser, "ChangePassword", input);
            var html = await result.Content.ReadAsStringAsync();
            Assert.Contains("validation-errors", html);
            Assert.DoesNotContain("value=\"" + OidcTestClient.Password, html);
            Assert.DoesNotContain("value=\"" + NewPassword, html);
            Assert.Equal(before.SecurityStamp, (await Current(client)).SecurityStamp);
        }
        var changed = await Post(client.Browser, "ChangePassword", ChangeInput());
        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.Contains("Password changed", await changed.Content.ReadAsStringAsync());
        Assert.Contains("expires=Thu, 01 Jan 1970", AuthenticationFactory.SessionCookie(changed));
        Assert.NotEqual(before.SecurityStamp, (await Current(client)).SecurityStamp);
        Assert.Equal(HttpStatusCode.Redirect, (await second.GetAsync("/Account")).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.Browser.GetAsync("/Account")).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await second.GetAsync(oldLink)).StatusCode);
        Assert.Equal("invalid_grant", await OidcTestClient.Error(await client.Refresh(tokens.GetProperty("refresh_token").GetString()!)));
        Assert.Equal(HttpStatusCode.OK, (await Login(second)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Login(second, NewPassword)).StatusCode);
    }

    [PostgresFact]
    public async Task WrongCurrentPasswordUsesExistingLockoutPolicyAndResetDoesNotRemoveLockoutOrTwoFactor()
    {
        var mail = new CapturedAccountMail();
        await using var client = await Client(mail);
        await Login(client.Browser);
        for (var attempt = 0; attempt < 5; attempt++)
            await Post(client.Browser, "ChangePassword", ChangeInput(current: "wrong"));
        var locked = await Current(client);
        Assert.True(locked.LockoutEnd > DateTimeOffset.UtcNow);
        Assert.Equal(HttpStatusCode.Redirect, (await client.Browser.GetAsync("/Account")).StatusCode);
        client.Database.Db.ChangeTracker.Clear();
        var user = await client.Database.Db.Users.SingleAsync();
        user.TwoFactorEnabled = true;
        await client.Database.Db.SaveChangesAsync();
        await Request(client.Browser);
        Assert.Equal(HttpStatusCode.OK, (await Reset(client.Browser, mail.Resets.Single().Path)).StatusCode);
        var reset = await Current(client);
        Assert.True(reset.TwoFactorEnabled);
        Assert.Equal(locked.LockoutEnd, reset.LockoutEnd);
        Assert.Equal(HttpStatusCode.OK, (await Login(client.Browser, NewPassword)).StatusCode);
    }

    [PostgresFact]
    public async Task ProfileIsAuthorizedValidatedAndOnlyEditsOwnNamesWithoutRenewingSession()
    {
        await using var client = await Client(new());
        foreach (var path in new[] { "/Account", "/Account/Profile", "/Account/ChangePassword" })
            Assert.Equal(HttpStatusCode.Redirect, (await client.Browser.GetAsync(path)).StatusCode);
        var login = await Login(client.Browser);
        var ticket = client.Factory.ReadTicket(login);
        var before = await Current(client);
        var claims = await client.Database.Db.UserClaims.AsNoTracking().Select(claim => claim.ClaimType + ":" + claim.ClaimValue).ToListAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.PostAsync("/Account/Profile", new FormUrlEncodedContent([]))).StatusCode);
        Dictionary<string, string> input = new() { ["FirstName"] = "Changed", ["LastName"] = "User", ["ScreenName"] = "Changed User" };
        foreach (var (field, value) in new[] { ("FirstName", " "), ("LastName", new string('n', 256)), ("ScreenName", "") })
        {
            var invalid = new Dictionary<string, string>(input) { [field] = value };
            Assert.Equal(HttpStatusCode.OK, (await Post(client.Browser, "Profile", invalid)).StatusCode);
            Assert.Equal(before.FirstName, (await Current(client)).FirstName);
        }
        input["FirstName"] = "  Edited  ";
        input["ScreenName"] = "<script>alert(1)</script>";
        input["UserId"] = input["Id"] = "someone-else";
        input["Email"] = input["UserName"] = "attacker@example.test";
        input["EmailConfirmed"] = "false";
        input["PasswordHash"] = input["SecurityStamp"] = "overposted";
        input["Claims[0].Type"] = "elevated";
        input["Claims[0].Value"] = "yes";
        var response = await Post(client.Browser, "Profile", input);
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var page = await client.Browser.GetStringAsync(response.Headers.Location);
        Assert.Contains("Your profile has been saved", page);
        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", page);
        Assert.DoesNotContain("<script>alert(1)</script>", page);
        var after = await Current(client);
        Assert.Equal("Edited", after.FirstName);
        Assert.Equal(input["ScreenName"], after.ScreenName);
        Assert.Equal(before.Email, after.Email);
        Assert.Equal(before.UserName, after.UserName);
        Assert.Equal(before.EmailConfirmed, after.EmailConfirmed);
        Assert.Equal(before.PasswordHash, after.PasswordHash);
        Assert.Equal(before.SecurityStamp, after.SecurityStamp);
        Assert.Equal(claims, await client.Database.Db.UserClaims.AsNoTracking().Select(claim => claim.ClaimType + ":" + claim.ClaimValue).ToListAsync());
        client.Factory.Clock.Advance(ticket.Properties.ExpiresUtc!.Value - client.Factory.Clock.GetUtcNow() + TimeSpan.FromSeconds(1));
        Assert.Equal(HttpStatusCode.Redirect, (await client.Browser.GetAsync("/Account")).StatusCode);
    }

    [PostgresFact]
    public async Task ProfileChangesAppearInUserinfoAndRenewedIdTokens()
    {
        await using var client = await Client(new());
        var tokens = await client.Tokens();
        await Post(client.Browser, "Profile", new() { ["FirstName"] = "New", ["LastName"] = "Name", ["ScreenName"] = "New screen" });
        var info = await (await client.UserInfo(tokens.GetProperty("access_token").GetString()!)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("New", info.GetProperty("given_name").GetString());
        Assert.Equal("Name", info.GetProperty("family_name").GetString());
        Assert.Equal("New screen", info.GetProperty("nickname").GetString());
        var refreshed = await (await client.Refresh(tokens.GetProperty("refresh_token").GetString()!)).Content.ReadFromJsonAsync<JsonElement>();
        var id = await client.ValidateIdToken(refreshed.GetProperty("id_token").GetString()!);
        Assert.Equal("New screen", id.GetClaim("nickname").Value);
        Assert.Equal(OidcTestClient.Email, id.GetClaim("name").Value);
    }

    [PostgresFact]
    public async Task RecoverySharesTheIpLimitWithRegistrationAndResend()
    {
        var mail = new CapturedAccountMail();
        await using var client = await Client(mail);
        for (var i = 0; i < 9; i++) await Request(client.Browser, "missing@example.test");
        await Post(client.Browser, "ResendConfirmation", new() { ["Email"] = "missing@example.test" });
        var response = await Request(client.Browser);
        Assert.Equal(HttpStatusCode.TooManyRequests, response.StatusCode);
        Assert.NotNull(response.Headers.RetryAfter);
        Assert.Empty(mail.Resets);
    }
}
