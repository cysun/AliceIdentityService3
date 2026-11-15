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
using OpenIddict.EntityFrameworkCore.Models;

namespace AliceIdentityService.Tests;

[Collection("PostgreSQL")]
public sealed class AdminTests
{
    private static readonly string[] Endpoints = ["/api/admin/session", "/api/admin/overview", "/api/admin/users", "/api/admin/users/missing"];

    private static async Task<HttpResponseMessage> Login(HttpClient browser, string email = OidcTestClient.Email, bool remember = false)
    {
        var html = await browser.GetStringAsync("/Account/Login");
        return await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = OidcTestClient.Password,
            ["RememberMe"] = remember.ToString(),
            ["__RequestVerificationToken"] = OidcTestClient.Field(html, "__RequestVerificationToken")
        }));
    }

    [PostgresFact]
    public async Task AdminEndpointsRequireTheCurrentAdministratorClaimAndNeverRedirectApiRequests()
    {
        await using var client = await OidcTestClient.Create();
        foreach (var endpoint in Endpoints)
        {
            var response = await client.Browser.GetAsync(endpoint);
            Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.True(response.Headers.CacheControl!.NoStore);
        }
        var users = client.Database.Services.GetRequiredService<UserManager<User>>();
        var standard = new User
        {
            UserName = "standard@example.test",
            Email = "standard@example.test",
            EmailConfirmed = true,
            FirstName = "Standard",
            LastName = "User",
            ScreenName = "Standard User"
        };
        Assert.True((await users.CreateAsync(standard, OidcTestClient.Password)).Succeeded);
        using var browser = client.Factory.Browser();
        Assert.Equal(HttpStatusCode.Redirect, (await Login(browser, standard.Email!)).StatusCode);
        foreach (var endpoint in Endpoints)
        {
            var response = await browser.GetAsync(endpoint);
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
            Assert.Null(response.Headers.Location);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.DoesNotContain("totalUsers", await response.Content.ReadAsStringAsync());
        }
        Assert.True((await users.AddClaimAsync(standard, new Claim(AisConstants.AdminClaim, "false"))).Succeeded);
        Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync(Endpoints[0])).StatusCode);
        Assert.True((await users.RemoveClaimAsync(standard, new Claim(AisConstants.AdminClaim, "false"))).Succeeded);
        foreach (var endpoint in Endpoints) Assert.Equal(HttpStatusCode.Forbidden, (await browser.GetAsync(endpoint)).StatusCode);
    }

    [PostgresFact]
    public async Task SessionReturnsCurrentSafeIdentityAndOriginalFixedExpiryForBothCookieLifetimes()
    {
        await using var client = await OidcTestClient.Create();
        foreach (var remember in new[] { false, true })
        {
            using var browser = client.Factory.Browser();
            var login = await Login(browser, remember: remember);
            var ticket = client.Factory.ReadTicket(login);
            var lifetime = remember ? TimeSpan.FromDays(14) : TimeSpan.FromHours(8);
            client.Factory.Clock.Advance(TimeSpan.FromHours(1));
            client.Database.Db.ChangeTracker.Clear();
            var user = await client.Database.Db.Users.SingleAsync();
            user.ScreenName = "Current <name>";
            await client.Database.Db.SaveChangesAsync();
            var response = await browser.GetAsync(Endpoints[0]);
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.True(response.Headers.CacheControl!.NoStore);
            Assert.False(response.Headers.Contains("Set-Cookie"));
            var json = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(["application", "expiresAt", "serverTime", "user"], json.EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal(["email", "firstName", "id", "lastName", "screenName"], json.GetProperty("user").EnumerateObject().Select(p => p.Name).Order().ToArray());
            Assert.Equal("Current <name>", json.GetProperty("user").GetProperty("screenName").GetString());
            Assert.Equal(user.Id, json.GetProperty("user").GetProperty("id").GetString());
            Assert.Equal(ticket.Properties.ExpiresUtc, json.GetProperty("expiresAt").GetDateTimeOffset());
            Assert.Equal(lifetime, json.GetProperty("expiresAt").GetDateTimeOffset() - ticket.Properties.IssuedUtc);
            Assert.Equal(client.Factory.Clock.GetUtcNow(), json.GetProperty("serverTime").GetDateTimeOffset());
            Assert.Equal(8, json.GetProperty("application").GetProperty("implementationStep").GetInt32());
            client.Factory.Clock.Advance(lifetime);
            foreach (var endpoint in Endpoints) Assert.Equal(HttpStatusCode.Unauthorized, (await browser.GetAsync(endpoint)).StatusCode);
        }
    }

    [PostgresFact]
    public async Task PasswordRevocationAndLogoutEndAdminSessionsOnTheirNextRequest()
    {
        await using var client = await OidcTestClient.Create();
        await Login(client.Browser);
        client.Database.Db.ChangeTracker.Clear();
        var user = await client.Database.Db.Users.SingleAsync();
        Assert.True((await client.Database.Services.GetRequiredService<UserManager<User>>().UpdateSecurityStampAsync(user)).Succeeded);
        foreach (var endpoint in Endpoints) Assert.Equal(HttpStatusCode.Unauthorized, (await client.Browser.GetAsync(endpoint)).StatusCode);
        await Login(client.Browser);
        var logoutPage = await client.Browser.GetStringAsync("/Account/Logout");
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.PostAsync("/Account/Logout", new FormUrlEncodedContent([]))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Browser.GetAsync(Endpoints[0])).StatusCode);
        var logout = await client.Browser.PostAsync("/Account/Logout", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = OidcTestClient.Field(logoutPage, "__RequestVerificationToken")
        }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        foreach (var endpoint in Endpoints) Assert.Equal(HttpStatusCode.Unauthorized, (await client.Browser.GetAsync(endpoint)).StatusCode);
    }

    [PostgresFact]
    public async Task OverviewCountsDistinctAdministratorsEnabledUnexpiredLockoutsAndOnlyReturnsBoundedSafeSummaries()
    {
        await using var client = await OidcTestClient.Create();
        var now = client.Factory.Clock.GetUtcNow();
        client.Database.Db.ChangeTracker.Clear();
        var admin = await client.Database.Db.Users.SingleAsync();
        admin.CreationTime = now.UtcDateTime.AddDays(-10);
        var users = client.Database.Services.GetRequiredService<UserManager<User>>();
        for (var index = 0; index < 7; index++)
        {
            var user = new User
            {
                Id = "new-user-" + index,
                UserName = $"new-{index}@example.test",
                Email = $"new-{index}@example.test",
                FirstName = "New",
                LastName = index.ToString(),
                ScreenName = "New user " + index,
                EmailConfirmed = index >= 2,
                LockoutEnabled = index != 3,
                CreationTime = now.UtcDateTime.AddDays(index >= 5 ? -1 : index - 7)
            };
            Assert.True((await users.CreateAsync(user, OidcTestClient.Password)).Succeeded);
            // Identity enables lockout on creation; set the imported disabled-lockout case afterward.
            user.LockoutEnabled = index != 3;
            user.LockoutEnd = index is 1 or 3 or 4 ? now.AddHours(1) : index == 2 ? now.AddHours(-1) : null;
            if (index == 5)
            {
                await users.AddClaimAsync(user, new Claim(AisConstants.AdminClaim, "false"));
                await users.AddClaimAsync(user, new Claim(AisConstants.AdminClaim, "another-value"));
            }
        }
        for (var index = 0; index < 7; index++)
            client.Database.Db.Add(new OpenIddictEntityFrameworkCoreApplication
            {
                Id = "extra-client-" + index,
                ClientId = "Application " + index,
                DisplayName = index == 0 ? null : index == 1 ? "" : "Application " + index,
                ClientSecret = "CLIENT-SECRET-MUST-NOT-LEAK",
                Properties = "{\"private\":\"PRIVATE-PROPERTY-MUST-NOT-LEAK\"}"
            });
        await client.Database.Db.SaveChangesAsync();
        await Login(client.Browser);
        var response = await client.Browser.GetAsync(Endpoints[1]);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
        Assert.False(response.Headers.Contains("Set-Cookie"));
        var result = (await response.Content.ReadFromJsonAsync<AdminOverview>())!;
        Assert.Equal(new AdminCounts(8, 9, 2, 2, 2), result.Counts);
        Assert.Equal(["new-user-5", "new-user-6", "new-user-4", "new-user-3", "new-user-2"], result.RecentRegistrations.Select(u => u.Id).ToArray());
        Assert.Equal(["Confirmed", "Confirmed", "Locked", "Confirmed", "Confirmed"], result.RecentRegistrations.Select(u => u.Status).ToArray());
        Assert.Equal(6, result.Applications.Count);
        Assert.Equal("Application 0", result.Applications[0].Name);
        Assert.Equal("Application 1", result.Applications[1].Name);
        Assert.Equal(now, result.AsOf);
        var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement;
        Assert.Equal(["createdAt", "email", "id", "name", "status"], json.GetProperty("recentRegistrations")[0].EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.Equal(["clientId", "id", "name"], json.GetProperty("applications")[0].EnumerateObject().Select(p => p.Name).Order().ToArray());
        Assert.DoesNotContain("MUST-NOT-LEAK", await response.Content.ReadAsStringAsync());
        client.Factory.Clock.Advance(TimeSpan.FromHours(2));
        var refreshed = (await client.Browser.GetFromJsonAsync<AdminOverview>(Endpoints[1]))!;
        Assert.Equal(0, refreshed.Counts.LockedUsers);
    }

    [PostgresFact]
    public async Task EmptyDatabaseOverviewHasZeroCountsAndNoInventedRecords()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        var result = await new AdminOverviewService(database.Db, TimeProvider.System).ReadAsync(default);
        Assert.Equal(new AdminCounts(0, 0, 0, 0, 0), result.Counts);
        Assert.Empty(result.RecentRegistrations);
        Assert.Empty(result.Applications);
    }

    [RestoredDatabaseFact]
    public async Task OverviewReadsTheUnmodifiedAis2Schema()
    {
        using var provider = PostgresTestDatabase.ServicesFor(Environment.GetEnvironmentVariable("AIS3_TEST_RESTORED_CONNECTION")!).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AisDbContext>();
        var overview = await new AdminOverviewService(database, TimeProvider.System).ReadAsync(default);
        Assert.Equal(await database.Users.LongCountAsync(), overview.Counts.TotalUsers);
        Assert.Equal(await database.Set<OpenIddictEntityFrameworkCoreApplication>().LongCountAsync(), overview.Counts.ConnectedApplications);
        Assert.InRange(overview.RecentRegistrations.Count, 0, 5);
        Assert.InRange(overview.Applications.Count, 0, 6);
    }
}
