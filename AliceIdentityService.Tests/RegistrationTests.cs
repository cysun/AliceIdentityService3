using System.Net;
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

internal sealed class CapturedAccountMail : IAccountEmailSender
{
    public List<(string Email, string Name, string Path)> Messages { get; } = [];
    public List<(string Email, string Name, string Path)> Resets { get; } = [];
    public bool Fail { get; set; }
    public Task SendPasswordResetAsync(User user, string resetPath, CancellationToken cancellationToken)
    {
        if (Fail) throw new MailDeliveryException();
        lock (Resets) Resets.Add((user.Email!, user.FullName, resetPath));
        return Task.CompletedTask;
    }
    public Task SendConfirmationAsync(User user, string confirmationPath, CancellationToken cancellationToken)
    {
        if (Fail) throw new MailDeliveryException();
        lock (Messages) Messages.Add((user.Email!, user.FullName, confirmationPath));
        return Task.CompletedTask;
    }
}

[Collection("PostgreSQL")]
public sealed class RegistrationTests
{
    private const string Email = "new@example.test";
    private const string Password = "Registration-Test-Password-42!";

    private static Dictionary<string, string> Input(string? context = null) => new()
    {
        ["Email"] = Email,
        ["FirstName"] = "New",
        ["LastName"] = "User",
        ["OidcContext"] = context ?? ""
    };

    private static async Task<HttpResponseMessage> Post(HttpClient browser, string action, Dictionary<string, string> input,
        string? getPath = null)
    {
        var html = await browser.GetStringAsync(getPath ?? "/Account/" + action);
        input["__RequestVerificationToken"] = OidcTestClient.Field(html, "__RequestVerificationToken");
        Assert.NotEmpty(input["__RequestVerificationToken"]);
        return await browser.PostAsync("/Account/" + action, new FormUrlEncodedContent(input));
    }

    private static Dictionary<string, string> Confirmation(string path) => QueryHelpers.ParseQuery(new Uri("https://localhost" + path).Query)
        .ToDictionary(pair => pair.Key, pair => pair.Value.ToString());

    private static Dictionary<string, string> ConfirmationFields(string path, string password, string? confirmPassword = null)
    {
        var fields = Confirmation(path);
        fields["Password"] = password;
        fields["ConfirmPassword"] = confirmPassword ?? password;
        return fields;
    }

    private static Task<HttpResponseMessage> Confirm(HttpClient browser, string path, string password = Password, string? confirmPassword = null)
        => Post(browser, "ConfirmEmail", ConfirmationFields(path, password, confirmPassword), path);

    private static async Task<PostgresTestDatabase> Database()
    {
        var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        return database;
    }

    private static AuthenticationFactory Factory(PostgresTestDatabase database, CapturedAccountMail mail) =>
        new(database.Db.Database.GetConnectionString()!, services => services.AddSingleton<IAccountEmailSender>(mail));

    [PostgresFact]
    public async Task RegistrationRequiresConfirmationThenPasswordSignInAndDoesNotGrantAdministration()
    {
        await using var database = await Database();
        var mail = new CapturedAccountMail();
        using var factory = Factory(database, mail);
        using var browser = factory.Browser();
        Assert.DoesNotContain("type=\"password\"", await browser.GetStringAsync("/Account/Register"));
        var result = await Post(browser, "Register", Input());
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var html = await result.Content.ReadAsStringAsync();
        Assert.Contains("Check your email", html);
        Assert.DoesNotContain(Password, html);
        Assert.False(result.Headers.Contains("Set-Cookie"));
        var user = await database.Db.Users.AsNoTracking().SingleAsync();
        Assert.False(user.EmailConfirmed);
        Assert.Null(user.PasswordHash);
        Assert.True(user.LockoutEnabled);
        Assert.Equal("New User", user.ScreenName);
        Assert.Empty(await database.Db.UserClaims.ToListAsync());
        Assert.Equal(HttpStatusCode.OK, (await Post(browser, "Login", new() { ["Email"] = Email, ["Password"] = Password })).StatusCode);
        var message = Assert.Single(mail.Messages);
        Assert.Equal(Email, message.Email);
        Assert.DoesNotContain(message.Path, html);
        var confirmPage = await browser.GetAsync(message.Path);
        Assert.Contains("autocomplete=\"new-password\"", await confirmPage.Content.ReadAsStringAsync());
        Assert.True(confirmPage.Headers.CacheControl!.NoStore);
        Assert.Equal("no-referrer", confirmPage.Headers.GetValues("Referrer-Policy").Single());
        Assert.False((await database.Db.Users.AsNoTracking().SingleAsync()).EmailConfirmed);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/Account/ConfirmEmail", new FormUrlEncodedContent(ConfirmationFields(message.Path, Password)))).StatusCode);
        var confirmed = await Confirm(browser, message.Path);
        Assert.Contains("Email confirmed", await confirmed.Content.ReadAsStringAsync());
        Assert.False(confirmed.Headers.Contains("Set-Cookie"));
        var current = await database.Db.Users.AsNoTracking().SingleAsync();
        Assert.True(current.EmailConfirmed);
        Assert.NotEqual(user.SecurityStamp, current.SecurityStamp);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync(message.Path)).StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await Post(browser, "Login", new() { ["Email"] = Email, ["Password"] = Password })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync("/api/system/info")).StatusCode);
    }

    [PostgresFact]
    public async Task RegistrationValidatesProfileFieldsAndConfirmationRequiresAValidNewPassword()
    {
        await using var database = await Database();
        var mail = new CapturedAccountMail();
        using var factory = Factory(database, mail);
        using var browser = factory.Browser();
        foreach (var route in new[] { "Register", "ResendConfirmation", "ConfirmEmail" })
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.PostAsync("/Account/" + route, new FormUrlEncodedContent(Input()))).StatusCode);
        foreach (var (field, value) in new[] { ("Email", "bad"), ("FirstName", " "), ("LastName", new string('n', 256)) })
        {
            var input = Input(); input[field] = value;
            var response = await Post(browser, "Register", input);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Contains("validation-errors", html);
        }
        Assert.Empty(await database.Db.Users.ToListAsync());
        Assert.Empty(mail.Messages);
        await Post(browser, "Register", Input());
        var path = Assert.Single(mail.Messages).Path;
        var pending = await database.Db.Users.AsNoTracking().SingleAsync();
        foreach (var (password, repeat) in new[] { ("", ""), (Password, "different"), ("weak", "weak") })
        {
            var response = await Confirm(browser, path, password, repeat);
            var html = await response.Content.ReadAsStringAsync();
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Contains("validation-summary-errors", html);
            Assert.DoesNotContain("value=\"" + Password, html);
            Assert.DoesNotContain("value=\"weak", html);
            var current = await database.Db.Users.AsNoTracking().SingleAsync();
            Assert.False(current.EmailConfirmed);
            Assert.Null(current.PasswordHash);
            Assert.Equal(pending.SecurityStamp, current.SecurityStamp);
        }
        Assert.Contains("Email confirmed", await (await Confirm(browser, path)).Content.ReadAsStringAsync());
    }

    [PostgresFact]
    public async Task ConfirmingALegacyPendingAccountReplacesItsPreviousPassword()
    {
        await using var database = await Database();
        var users = database.Services.GetRequiredService<UserManager<User>>();
        var pending = new User { UserName = Email, Email = Email, FirstName = "Legacy", LastName = "User", ScreenName = "Legacy User" };
        const string previousPassword = "Previous-Test-Password-42!";
        Assert.True((await users.CreateAsync(pending, previousPassword)).Succeeded);
        var mail = new CapturedAccountMail();
        using var factory = Factory(database, mail);
        using var browser = factory.Browser();
        await Post(browser, "Register", Input());
        var path = Assert.Single(mail.Messages).Path;
        Assert.Contains("Email confirmed", await (await Confirm(browser, path)).Content.ReadAsStringAsync());
        var confirmed = await database.Db.Users.AsNoTracking().SingleAsync();
        Assert.True(confirmed.EmailConfirmed);
        Assert.True(await users.CheckPasswordAsync(confirmed, Password));
        Assert.False(await users.CheckPasswordAsync(confirmed, previousPassword));
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync(path)).StatusCode);
    }

    [PostgresFact]
    public async Task DuplicateRegistrationNeverChangesPasswordOrProfileAndResendsAreLimited()
    {
        await using var database = await Database();
        var mail = new CapturedAccountMail();
        using var factory = Factory(database, mail);
        using var browser = factory.Browser();
        await Post(browser, "Register", Input());
        var original = await database.Db.Users.AsNoTracking().SingleAsync();
        var duplicate = Input(); duplicate["Email"] = " NEW@EXAMPLE.TEST "; duplicate["FirstName"] = "Changed";
        duplicate["Password"] = duplicate["ConfirmPassword"] = "Another-Password-42!";
        var response = await Post(browser, "Register", duplicate);
        Assert.Contains("Check your email", await response.Content.ReadAsStringAsync());
        var current = await database.Db.Users.AsNoTracking().SingleAsync();
        Assert.Equal(original.PasswordHash, current.PasswordHash);
        Assert.Equal(original.FirstName, current.FirstName);
        Assert.Single(mail.Messages);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        await Post(browser, "ResendConfirmation", new() { ["Email"] = Email });
        Assert.Equal(2, mail.Messages.Count);
        await Confirm(browser, mail.Messages[^1].Path);
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        var confirmed = await Post(browser, "ResendConfirmation", new() { ["Email"] = Email });
        var unknown = await Post(browser, "ResendConfirmation", new() { ["Email"] = "missing@example.test" });
        Assert.Equal(confirmed.StatusCode, unknown.StatusCode);
        Assert.Contains("Check your email", await confirmed.Content.ReadAsStringAsync());
        Assert.Contains("Check your email", await unknown.Content.ReadAsStringAsync());
        Assert.Equal(2, mail.Messages.Count);
    }

    [PostgresFact]
    public async Task FailedPublicationLeavesAnUnconfirmedAccountThatCanBeRetriedWithoutRegistration()
    {
        await using var database = await Database();
        var mail = new CapturedAccountMail { Fail = true };
        using var log = new TestLogFile();
        using var factory = new AuthenticationFactory(database.Db.Database.GetConnectionString()!,
            services => services.AddSingleton<IAccountEmailSender>(mail), log);
        using var browser = factory.Browser();
        var result = await Post(browser, "Register", Input());
        Assert.Equal(HttpStatusCode.ServiceUnavailable, result.StatusCode);
        Assert.Contains("could not queue", await result.Content.ReadAsStringAsync());
        Assert.False((await database.Db.Users.AsNoTracking().SingleAsync()).EmailConfirmed);
        Assert.Contains(log.Lines(), line => line.Contains("A new account created for " + Email));
        await Post(browser, "Register", Input());
        mail.Fail = false;
        factory.Clock.Advance(TimeSpan.FromMinutes(1));
        Assert.Equal(HttpStatusCode.OK, (await Post(browser, "ResendConfirmation", new() { ["Email"] = Email })).StatusCode);
        var link = Assert.Single(mail.Messages).Path;
        await Confirm(browser, link, "weak");
        Assert.DoesNotContain(log.Lines(), line => line.Contains("confirmed its email"));
        await Confirm(browser, link);
        Assert.True((await database.Db.Users.AsNoTracking().SingleAsync()).EmailConfirmed);
        var lines = log.Lines();
        Assert.Single(lines, line => line.Contains("A new account created"));
        Assert.Single(lines, line => line.Contains(Email) && line.Contains("confirmed its email"));
        Assert.DoesNotContain(lines, line => line.Contains(Password) || line.Contains(Confirmation(link)["code"]));
    }

    [PostgresFact]
    public async Task ConfirmationRejectsWrongUsersTamperingExpiredTokensAndChangedSecurityStamps()
    {
        await using var database = await Database();
        var mail = new CapturedAccountMail();
        using var factory = Factory(database, mail);
        using var browser = factory.Browser();
        await Post(browser, "Register", Input());
        var path = mail.Messages.Single().Path;
        var fields = Confirmation(path);
        foreach (var (key, value) in new[] { ("code", "not-a-token"), ("userId", "missing") })
        {
            var invalid = new Dictionary<string, string>(fields) { [key] = value };
            Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync(QueryHelpers.AddQueryString("/Account/ConfirmEmail", invalid!))).StatusCode);
        }
        var options = factory.Services.GetRequiredService<IOptions<EmailConfirmationTokenOptions>>().Value;
        options.TokenLifespan = TimeSpan.FromSeconds(-1);
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync(path)).StatusCode);
        options.TokenLifespan = TimeSpan.FromHours(24);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(path)).StatusCode);
        var user = await database.Db.Users.SingleAsync(); user.SecurityStamp = Guid.NewGuid().ToString();
        await database.Db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.BadRequest, (await browser.GetAsync(path)).StatusCode);
        Assert.False(user.EmailConfirmed);
    }

    [PostgresFact]
    public async Task OidcRegistrationConfirmsThenResumesValidatedAuthorizationAfterPasswordLogin()
    {
        var mail = new CapturedAccountMail();
        await using var client = await OidcTestClient.Create(services => services.AddSingleton<IAccountEmailSender>(mail));
        var login = await (await client.Authorize()).Content.ReadAsStringAsync();
        var context = OidcTestClient.Field(login, "OidcContext");
        var registerPath = "/Account/Register?context=" + Uri.EscapeDataString(context);
        var invalid = Input(context); invalid["FirstName"] = "";
        var errorHtml = await (await Post(client.Browser, "Register", invalid, registerPath)).Content.ReadAsStringAsync();
        Assert.Contains("account-shell oidc", errorHtml);
        Assert.Equal(context, OidcTestClient.Field(errorHtml, "OidcContext"));
        await Post(client.Browser, "Register", Input(context), registerPath);
        var message = Assert.Single(mail.Messages);
        Assert.Equal(context, Confirmation(message.Path)["oidcContext"]);
        var confirmed = await Confirm(client.Browser, message.Path);
        Assert.Contains("account-shell oidc", await confirmed.Content.ReadAsStringAsync());
        var next = await Post(client.Browser, "Login", new() { ["Email"] = Email, ["Password"] = Password, ["OidcContext"] = context },
            "/Account/Login?context=" + Uri.EscapeDataString(context));
        var authorize = await client.Browser.GetAsync(next.Headers.Location);
        Assert.StartsWith(OidcTestClient.Callback, authorize.Headers.Location!.AbsoluteUri);
        var query = QueryHelpers.ParseQuery(authorize.Headers.Location.Query);
        Assert.Equal(client.State, query["state"].ToString());
        Assert.Equal(HttpStatusCode.OK, (await client.Redeem(query["code"]!)).StatusCode);
    }

    [PostgresFact]
    public async Task ExpiredOidcContextDoesNotPreventConfirmationOrCreateAnUnsafeRedirect()
    {
        var mail = new CapturedAccountMail();
        await using var client = await OidcTestClient.Create(services => services.AddSingleton<IAccountEmailSender>(mail));
        await Post(client.Browser, "Register", Input());
        var expired = client.Factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("AIS3.OidcLoginContext.v1").ToTimeLimitedDataProtector()
            .Protect(JsonSerializer.Serialize(new ValidatedLoginContext(OidcTestClient.ClientId, "/connect/authorize")), DateTimeOffset.UtcNow.AddMinutes(-1));
        foreach (var context in new[] { expired, "https://evil.example" })
            Assert.Equal(HttpStatusCode.BadRequest, (await Post(client.Browser, "Register", Input(context))).StatusCode);
        var path = QueryHelpers.AddQueryString(mail.Messages.Single().Path, "oidcContext", expired);
        var confirmed = await Confirm(client.Browser, path);
        var html = await confirmed.Content.ReadAsStringAsync();
        Assert.Contains("application sign-in request has expired", html);
        Assert.DoesNotContain("/Account/Login?context=", html);
        Assert.Null(confirmed.Headers.Location);
    }

    [PostgresFact]
    public async Task ConcurrentRegistrationsKeepOneAccountAndOneInitialMessage()
    {
        await using var database = await Database();
        var mail = new CapturedAccountMail();
        using var factory = Factory(database, mail);
        using var first = factory.Browser(); using var second = factory.Browser();
        var results = await Task.WhenAll(Post(first, "Register", Input()), Post(second, "Register", Input()));
        Assert.All(results, response => Assert.Equal(HttpStatusCode.OK, response.StatusCode));
        Assert.Single(await database.Db.Users.ToListAsync());
        Assert.Single(mail.Messages);
    }

    [PostgresFact]
    public async Task ConcurrentConfirmationsActivateOnlyTheWinningPassword()
    {
        await using var database = await Database();
        var mail = new CapturedAccountMail();
        using var factory = Factory(database, mail);
        using var first = factory.Browser();
        using var second = factory.Browser();
        await Post(first, "Register", Input());
        var path = Assert.Single(mail.Messages).Path;
        const string secondPassword = "Second-Test-Password-42!";
        var firstFields = ConfirmationFields(path, Password);
        var secondFields = ConfirmationFields(path, secondPassword);
        firstFields["__RequestVerificationToken"] = OidcTestClient.Field(await first.GetStringAsync(path), "__RequestVerificationToken");
        secondFields["__RequestVerificationToken"] = OidcTestClient.Field(await second.GetStringAsync(path), "__RequestVerificationToken");

        var results = await Task.WhenAll(
            first.PostAsync("/Account/ConfirmEmail", new FormUrlEncodedContent(firstFields)),
            second.PostAsync("/Account/ConfirmEmail", new FormUrlEncodedContent(secondFields)));
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(results, result => result.StatusCode == HttpStatusCode.BadRequest);
        var confirmed = await database.Db.Users.AsNoTracking().SingleAsync();
        var users = database.Services.GetRequiredService<UserManager<User>>();
        Assert.True(confirmed.EmailConfirmed);
        Assert.Equal(results[0].IsSuccessStatusCode, await users.CheckPasswordAsync(confirmed, Password));
        Assert.Equal(results[1].IsSuccessStatusCode, await users.CheckPasswordAsync(confirmed, secondPassword));
    }

    [PostgresFact]
    public async Task IpLimitRejectsExcessRequestsWithoutSendingMail()
    {
        await using var database = await Database();
        var mail = new CapturedAccountMail();
        using var factory = Factory(database, mail);
        using var browser = factory.Browser();
        for (var i = 0; i < 10; i++)
            Assert.Equal(HttpStatusCode.OK, (await Post(browser, "ResendConfirmation", new() { ["Email"] = "missing@example.test" })).StatusCode);
        var limited = await Post(browser, "ResendConfirmation", new() { ["Email"] = Email });
        Assert.Equal(HttpStatusCode.TooManyRequests, limited.StatusCode);
        Assert.NotNull(limited.Headers.RetryAfter);
        Assert.Empty(mail.Messages);
    }

    [Fact]
    public void RecipientLimitSharesCaseNormalizedKeysAndEnforcesCooldownAndHourlyCap()
    {
        var clock = new TestClock();
        using var limit = new AccountEmailLimit(clock);
        for (var i = 0; i < 5; i++)
        {
            Assert.True(limit.TryAcquire("USER@EXAMPLE.TEST"));
            Assert.False(limit.TryAcquire("USER@EXAMPLE.TEST"));
            clock.Advance(TimeSpan.FromMinutes(1));
        }
        Assert.False(limit.TryAcquire("USER@EXAMPLE.TEST"));
        clock.Advance(TimeSpan.FromHours(1));
        Assert.True(limit.TryAcquire("USER@EXAMPLE.TEST"));
    }
}
