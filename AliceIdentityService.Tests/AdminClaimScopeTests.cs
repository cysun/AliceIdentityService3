using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Tests;

[Collection("PostgreSQL")]
public sealed class AdminClaimScopeTests
{
    private static async Task Login(HttpClient browser, string email = OidcTestClient.Email)
    {
        var html = await browser.GetStringAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = OidcTestClient.Password,
            ["__RequestVerificationToken"] = OidcTestClient.Field(html, "__RequestVerificationToken")
        }))).StatusCode);
    }

    private static async Task<User> AddUser(OidcTestClient client, bool admin = false)
    {
        await using var scope = client.Factory.Services.CreateAsyncScope();
        var users = scope.ServiceProvider.GetRequiredService<UserManager<User>>();
        var user = new User { Email = Guid.NewGuid() + "@example.test", FirstName = "Test", LastName = "User", ScreenName = "Test User", EmailConfirmed = true };
        user.UserName = user.Email;
        Assert.True((await users.CreateAsync(user, OidcTestClient.Password)).Succeeded);
        if (admin) Assert.True((await users.AddClaimAsync(user, new Claim(AisConstants.AdminClaim, ""))).Succeeded);
        return user;
    }

    private static async Task<HttpResponseMessage> Write(HttpClient browser, string path, object input, bool delete = false, string? csrf = null)
    {
        using var message = new HttpRequestMessage(delete ? HttpMethod.Delete : HttpMethod.Post, "/api/admin/" + path) { Content = JsonContent.Create(input) };
        if (csrf is null)
            csrf = (await browser.GetFromJsonAsync<JsonElement>("/api/admin/antiforgery")).GetProperty("requestToken").GetString();
        if (csrf != "missing") message.Headers.Add("X-CSRF-TOKEN", csrf);
        return await browser.SendAsync(message);
    }
    private static async Task Ok(HttpResponseMessage response) => Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    private static Task<AdminUserDetail?> UserDetail(HttpClient browser, string id) => browser.GetFromJsonAsync<AdminUserDetail>("/api/admin/users/" + id);
    private static Task<AdminScopeDetail?> ScopeDetail(HttpClient browser, string id) => browser.GetFromJsonAsync<AdminScopeDetail>("/api/admin/scopes/" + id);
    private static object ScopeInput(string name = "courses", string[]? claims = null, string[]? resources = null, string? version = null) => new
    { name, displayName = "Courses <test>", description = "Course access", claims = claims ?? ["department", "role"], resources = resources ?? ["courses-api"], version };
    private static async Task<string> CreateScope(HttpClient browser, string name = "courses")
    {
        var response = await Write(browser, "scopes", ScopeInput(name));
        await Ok(response);
        return (await response.Content.ReadFromJsonAsync<AdminMutationResult>())!.Id;
    }

    [PostgresFact]
    public async Task ClaimsAndScopesRequireAdministratorAndAntiforgeryForEveryWrite()
    {
        await using var client = await OidcTestClient.Create();
        var target = await AddUser(client);
        using var ordinary = client.Factory.Browser();
        await Login(ordinary, target.Email!);
        var paths = new[] { ("scopes", false), ("scopes/missing", false), ("scopes/missing", true), ("users/" + target.Id + "/claims", false), ("users/" + target.Id + "/claims/1", true) };
        foreach (var browser in new[] { client.Browser, ordinary })
        {
            var expected = browser == ordinary ? HttpStatusCode.Forbidden : HttpStatusCode.Unauthorized;
            foreach (var path in new[] { "scopes", "scopes/missing" })
                Assert.Equal(expected, (await browser.GetAsync("/api/admin/" + path)).StatusCode);
            foreach (var (path, delete) in paths)
                Assert.Equal(expected, (await Write(browser, path, new { }, delete, "missing")).StatusCode);
        }
        await Login(client.Browser);
        foreach (var (path, delete) in paths)
            foreach (var token in new[] { "missing", "forged" })
                Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, path, new { }, delete, token)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.Browser.GetAsync("/api/admin/users/" + target.Id + "/claims")).StatusCode);
        Assert.True((await client.Browser.GetAsync("/api/admin/scopes")).Headers.CacheControl!.NoStore);
    }

    [PostgresFact]
    public async Task ClaimsValidateDuplicatesReservedTypesAndVersionsAndDeleteSpecificLegacyRows()
    {
        using var log = new TestLogFile();
        await using var client = await OidcTestClient.Create(log: log);
        await Login(client.Browser);
        var target = await AddUser(client);
        var detail = (await UserDetail(client.Browser, target.Id))!;
        var path = "users/" + target.Id + "/claims";
        foreach (var type in new[] { " ", "bad type", "sub", "name", "email", "oi_scopes", "AspNet.Identity.SecurityStamp", "aud", "at_hash", ClaimTypes.NameIdentifier, new string('a', 257) })
            Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, path, new { detail.Version, type, value = "x" })).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, path, new { detail.Version, type = "department", value = "a\nb" })).StatusCode);
        await Ok(await Write(client.Browser, path, new { detail.Version, type = " department ", value = " <script>test</script> ", userId = "wrong" }));
        Assert.Equal(HttpStatusCode.Conflict, (await Write(client.Browser, path, new { detail.Version, type = "role", value = "staff" })).StatusCode);
        detail = (await UserDetail(client.Browser, target.Id))!;
        var stored = Assert.Single(detail.Claims);
        Assert.Equal("department", stored.Type);
        Assert.Equal("<script>test</script>", stored.Value);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, path, new { detail.Version, type = stored.Type, value = stored.Value })).StatusCode);
        await Ok(await Write(client.Browser, path, new { detail.Version, type = "department", value = "Other" }));
        detail = (await UserDetail(client.Browser, target.Id))!;
        await Ok(await Write(client.Browser, path, new { detail.Version, type = "empty", value = "" }));
        // AIS2 can contain duplicate and null rows. Delete by owned ID, never by matching value alone.
        client.Database.Db.UserClaims.AddRange(new IdentityUserClaim<string> { UserId = target.Id, ClaimType = stored.Type, ClaimValue = stored.Value },
            new IdentityUserClaim<string> { UserId = target.Id, ClaimType = null, ClaimValue = null });
        await client.Database.Db.SaveChangesAsync();
        detail = (await UserDetail(client.Browser, target.Id))!;
        await Ok(await Write(client.Browser, path + "/" + stored.Id, new { detail.Version }, true));
        detail = (await UserDetail(client.Browser, target.Id))!;
        Assert.Single(detail.Claims, claim => claim.Value == stored.Value);
        var nullClaim = Assert.Single(detail.Claims, claim => claim.Type is null);
        await Ok(await Write(client.Browser, path + "/" + nullClaim.Id, new { detail.Version }, true));
        detail = (await UserDetail(client.Browser, target.Id))!;
        var actor = await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Email == OidcTestClient.Email);
        var otherClaim = await client.Database.Db.UserClaims.AsNoTracking().FirstAsync(claim => claim.UserId == actor.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, path + "/" + otherClaim.Id, new { detail.Version }, true)).StatusCode);
        var lines = log.Lines();
        var added = lines.Where(line => line.Contains("added a claim to")).ToArray();
        Assert.Equal(3, added.Length);
        Assert.Contains(added, line => line.Contains(actor.Email!) && line.Contains(target.Id) && line.Contains("department") && line.Contains("ClaimId"));
        Assert.DoesNotContain(lines, line => line.Contains(stored.Value!));
    }

    [PostgresFact]
    public async Task ClaimChangesRevokeRealCookiesCodesAndRefreshTokensAndFreshTokensUseCurrentAssignments()
    {
        await using var client = await OidcTestClient.Create();
        var tokens = await client.Tokens();
        var code = await client.Code();
        var target = await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Email == OidcTestClient.Email);
        var actor = await AddUser(client, true);
        using var admin = client.Factory.Browser();
        await Login(admin, actor.Email!);
        var detail = (await UserDetail(admin, target.Id))!;
        await Ok(await Write(admin, "users/" + target.Id + "/claims", new { detail.Version, type = "department", value = "Mathematics" }));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.Browser.GetAsync("/api/admin/session")).StatusCode);
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Redeem(code)));
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Refresh(tokens.GetProperty("refresh_token").GetString()!)));
        var fresh = await client.Tokens();
        var idToken = await client.ValidateIdToken(fresh.GetProperty("id_token").GetString()!);
        Assert.Equal(new[] { "Computing", "Mathematics" }, idToken.Claims.Where(claim => claim.Type == "department").Select(claim => claim.Value).Order().ToArray());
        Assert.False(idToken.TryGetClaim("ais_admin", out _));
        detail = (await UserDetail(admin, target.Id))!;
        var claim = detail.Claims.Single(claim => claim.Value == "Mathematics");
        await Ok(await Write(admin, "users/" + target.Id + "/claims/" + claim.Id, new { detail.Version }, true));
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Refresh(fresh.GetProperty("refresh_token").GetString()!)));
        fresh = await client.Tokens();
        idToken = await client.ValidateIdToken(fresh.GetProperty("id_token").GetString()!);
        Assert.Equal("Computing", Assert.Single(idToken.Claims, claim => claim.Type == "department").Value);
        var parameters = client.Parameters(); parameters["scope"] = "openid";
        var minimal = await (await client.Redeem(await client.Code(parameters))).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False((await client.ValidateIdToken(minimal.GetProperty("id_token").GetString()!)).TryGetClaim("department", out _));
    }

    [PostgresFact]
    public async Task OwnLastAdministratorClaimIsProtectedAndOwnChangesEndTheSession()
    {
        await using var client = await OidcTestClient.Create();
        await Login(client.Browser);
        var actor = await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Email == OidcTestClient.Email);
        var detail = (await UserDetail(client.Browser, actor.Id))!;
        var claim = Assert.Single(detail.Claims, claim => claim.Type == AisConstants.AdminClaim);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "users/" + actor.Id + "/claims/" + claim.Id, new { detail.Version }, true)).StatusCode);
        var response = await Write(client.Browser, "users/" + actor.Id + "/claims", new { detail.Version, type = AisConstants.AdminClaim, value = "false" });
        await Ok(response);
        Assert.True((await response.Content.ReadFromJsonAsync<AdminMutationResult>())!.SessionEnded);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.Browser.GetAsync("/api/admin/session")).StatusCode);
        await Login(client.Browser);
        detail = (await UserDetail(client.Browser, actor.Id))!;
        await Ok(await Write(client.Browser, "users/" + actor.Id + "/claims/" + claim.Id, new { detail.Version }, true));
        await Login(client.Browser);
        Assert.Equal(HttpStatusCode.OK, (await client.Browser.GetAsync("/api/admin/session")).StatusCode);
    }

    [PostgresFact]
    public async Task ScopeCrudValidatesDuplicatesPreservesImportedMetadataAndProtectsStandardDefinitions()
    {
        await using var client = await OidcTestClient.Create();
        await Login(client.Browser);
        foreach (var input in new[] { ScopeInput("two words"), ScopeInput("openid"), ScopeInput("offline_access"), ScopeInput("courses", ["oi_scopes"]),
            ScopeInput("courses", ["AspNet.Identity.SecurityStamp"]), ScopeInput("courses", ["role", " role "]), ScopeInput("courses", resources: ["a", "a"]),
            ScopeInput("courses", [null!]), ScopeInput("courses", resources: [null!]), ScopeInput("courses", resources: ["two words"]) })
            Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "scopes", input)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "scopes", new { name = "missing-lists" })).StatusCode);
        var id = await CreateScope(client.Browser);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "scopes", ScopeInput())).StatusCode);
        await using (var scope = client.Factory.Services.CreateAsyncScope())
        {
            var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
            var record = (await scopes.FindByIdAsync(id))!;
            var descriptor = new OpenIddictScopeDescriptor();
            await scopes.PopulateAsync(descriptor, record);
            descriptor.Properties["imported"] = JsonSerializer.SerializeToElement(new { preserved = true });
            descriptor.DisplayNames[System.Globalization.CultureInfo.GetCultureInfo("fr")] = "Cours";
            await scopes.UpdateAsync(record, descriptor);
        }
        var detail = (await ScopeDetail(client.Browser, id))!;
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "scopes/" + id, ScopeInput("renamed", version: detail.Version))).StatusCode);
        await Ok(await Write(client.Browser, "scopes/" + id, ScopeInput(claims: ["role"], resources: [], version: detail.Version)));
        Assert.Equal(HttpStatusCode.Conflict, (await Write(client.Browser, "scopes/" + id, ScopeInput(version: detail.Version))).StatusCode);
        var edited = (await ScopeDetail(client.Browser, id))!;
        Assert.Equal(new[] { "role" }, edited.Claims);
        Assert.Empty(edited.Resources);
        var recordJson = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreScope>().AsNoTracking().SingleAsync(scope => scope.Id == id);
        Assert.Contains("imported", recordJson.Properties);
        Assert.Contains("Cours", recordJson.DisplayNames);
        Assert.DoesNotContain("imported", await client.Browser.GetStringAsync("/api/admin/scopes/" + id));
        Assert.DoesNotContain(recordJson.ConcurrencyToken!, await client.Browser.GetStringAsync("/api/admin/scopes/" + id));
        var standard = (await client.Browser.GetFromJsonAsync<AdminScopePage>("/api/admin/scopes?q=profile"))!.Items.Single();
        Assert.True(standard.Standard);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "scopes/" + standard.Id, ScopeInput("profile", version: standard.Version))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "scopes/" + standard.Id, new { standard.Version, confirmation = standard.Name }, true)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "scopes/" + id, new { edited.Version, confirmation = "wrong" }, true)).StatusCode);
        await Ok(await Write(client.Browser, "scopes/" + id, new { edited.Version, confirmation = edited.Name }, true));
        Assert.Equal(HttpStatusCode.NotFound, (await client.Browser.GetAsync("/api/admin/scopes/" + id)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Write(client.Browser, "scopes/" + id, ScopeInput(version: edited.Version))).StatusCode);
    }

    [PostgresFact]
    public async Task ScopeChangesAffectRenewedTokensAndUserInfoWithoutExposingPrivateClaims()
    {
        await using var client = await OidcTestClient.Create();
        var issued = await client.Tokens();
        Assert.Contains("department-api", new JsonWebToken(issued.GetProperty("access_token").GetString()).Audiences);
        // Change mappings on a second host so the token issuer must observe the persisted edit.
        await using var adminHost = new AuthenticationFactory(client.Database.Db.Database.GetConnectionString()!);
        using var admin = adminHost.Browser();
        await Login(admin);
        var scope = (await admin.GetFromJsonAsync<AdminScopePage>("/api/admin/scopes?q=department"))!.Items.Single();
        await Ok(await Write(admin, "scopes/" + scope.Id, ScopeInput("department", ["email"], ["new-api"], scope.Version)));
        var info = await (await client.UserInfo(issued.GetProperty("access_token").GetString()!)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.False(info.TryGetProperty("department", out _));
        var refreshed = await client.Refresh(issued.GetProperty("refresh_token").GetString()!);
        await Ok(refreshed);
        var tokens = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        var identity = await client.ValidateIdToken(tokens.GetProperty("id_token").GetString()!);
        var access = new JsonWebToken(tokens.GetProperty("access_token").GetString());
        Assert.False(identity.TryGetClaim("department", out _));
        Assert.False(access.TryGetClaim("department", out _));
        Assert.Contains("new-api", access.Audiences);
        Assert.DoesNotContain("department-api", access.Audiences);
        Assert.False(identity.TryGetClaim("AspNet.Identity.SecurityStamp", out _));
        Assert.False(identity.TryGetClaim("oi_scopes", out _));
        var outstandingCode = await client.Code();
        var edited = (await ScopeDetail(admin, scope.Id))!;
        await Ok(await Write(admin, "scopes/" + scope.Id, ScopeInput("department", ["email"], [], edited.Version)));
        foreach (var response in new[] { await client.Refresh(tokens.GetProperty("refresh_token").GetString()!), await client.Redeem(outstandingCode) })
        {
            await Ok(response);
            var updated = await response.Content.ReadFromJsonAsync<JsonElement>();
            Assert.Empty(new JsonWebToken(updated.GetProperty("access_token").GetString()).Audiences);
        }
        // Already-issued self-contained JWT content does not change.
        Assert.Equal("Computing", new JsonWebToken(issued.GetProperty("access_token").GetString()).GetClaim("department").Value);
    }

    [PostgresFact]
    public async Task ScopeDeletionRejectsClientReferencesAndActiveGrantsAndSearchIsBounded()
    {
        await using var client = await OidcTestClient.Create();
        await Login(client.Browser);
        var department = (await client.Browser.GetFromJsonAsync<AdminScopePage>("/api/admin/scopes?q=department"))!.Items.Single();
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "scopes/" + department.Id, new { department.Version, confirmation = department.Name }, true)).StatusCode);
        var id = await CreateScope(client.Browser);
        var detail = (await ScopeDetail(client.Browser, id))!;
        await using (var scope = client.Factory.Services.CreateAsyncScope())
        {
            var grants = scope.ServiceProvider.GetRequiredService<IOpenIddictAuthorizationManager>();
            await grants.CreateAsync(new OpenIddictAuthorizationDescriptor { Status = Statuses.Valid, Type = AuthorizationTypes.Permanent, Scopes = { "courses" } });
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "scopes/" + id, new { detail.Version, confirmation = detail.Name }, true)).StatusCode);
        var page = (await client.Browser.GetFromJsonAsync<AdminScopePage>("/api/admin/scopes?pageSize=2&page=999"))!;
        Assert.InRange(page.Items.Count, 1, 2);
        Assert.Equal((page.TotalCount + 1) / 2, page.Page);
        Assert.Empty((await client.Browser.GetFromJsonAsync<AdminScopePage>("/api/admin/scopes?q=%25"))!.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.GetAsync("/api/admin/scopes?pageSize=101")).StatusCode);
    }

    [PostgresFact]
    public async Task ConcurrentClaimWritesCannotRemoveBothAdministratorsAndScopeEditsRejectStaleVersions()
    {
        await using var client = await OidcTestClient.Create();
        var first = await AddUser(client, true);
        var second = await AddUser(client, true);
        await using var setup = client.Factory.Services.CreateAsyncScope();
        var signIn = setup.ServiceProvider.GetRequiredService<SignInManager<User>>();
        var firstPrincipal = await signIn.CreateUserPrincipalAsync(first);
        var secondPrincipal = await signIn.CreateUserPrincipalAsync(second);
        var claims = await client.Database.Db.UserClaims.AsNoTracking().Where(claim => claim.UserId == first.Id || claim.UserId == second.Id).ToDictionaryAsync(claim => claim.UserId, claim => claim.Id);
        async Task<AdminMutationOutcome> Remove(ClaimsPrincipal principal, User target)
        {
            await using var scope = client.Factory.Services.CreateAsyncScope();
            return await scope.ServiceProvider.GetRequiredService<AdminUserMutationService>().RemoveClaimAsync(principal, target.Id, claims[target.Id],
                new() { Version = AdminUserVersion.For(target.Id, target.ConcurrencyStamp) }, CancellationToken.None);
        }
        var results = await Task.WhenAll(Remove(firstPrincipal, second), Remove(secondPrincipal, first));
        Assert.Single(results, result => result.Status == 200);
        Assert.Single(results, result => result.Status == 401);
        await Login(client.Browser);
        var id = await CreateScope(client.Browser);
        var detail = (await ScopeDetail(client.Browser, id))!;
        var csrf = (await client.Browser.GetFromJsonAsync<JsonElement>("/api/admin/antiforgery")).GetProperty("requestToken").GetString()!;
        var edits = await Task.WhenAll(Write(client.Browser, "scopes/" + id, ScopeInput(version: detail.Version), csrf: csrf),
            Write(client.Browser, "scopes/" + id, ScopeInput(version: detail.Version), csrf: csrf));
        Assert.Single(edits, result => result.StatusCode == HttpStatusCode.OK);
        Assert.Single(edits, result => result.StatusCode == HttpStatusCode.Conflict);
    }

    [PostgresFact]
    public async Task ClaimAndStampChangesRollBackWhenRevocationFails()
    {
        using var log = new TestLogFile();
        await using var client = await OidcTestClient.Create(log: log);
        await client.Tokens();
        var target = await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Email == OidcTestClient.Email);
        var actor = await AddUser(client, true);
        using var admin = client.Factory.Browser();
        await Login(admin, actor.Email!);
        var detail = (await UserDetail(admin, target.Id))!;
        var previousTokens = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().OrderBy(token => token.Id).Select(token => new { token.Id, token.Status }).ToListAsync();
        await client.Database.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_claim_revoke() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Deliberate test revocation failure'; END; $$;
            CREATE TRIGGER fail_claim_revoke BEFORE UPDATE ON "OpenIddictAuthorizations"
            FOR EACH ROW EXECUTE FUNCTION fail_claim_revoke();
            """);
        foreach (var delete in new[] { false, true })
        {
            var path = "users/" + target.Id + "/claims" + (delete ? "/" + detail.Claims.First().Id : "");
            await Assert.ThrowsAsync<PostgresException>(() => Write(admin, path, new { detail.Version, type = "role", value = "new" }, delete));
            var unchanged = (await UserDetail(admin, target.Id))!;
            Assert.Equal(detail.Version, unchanged.Version);
            Assert.Equal(detail.Claims, unchanged.Claims);
            Assert.Equal(target.SecurityStamp, (await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Id == target.Id)).SecurityStamp);
            Assert.Equal(previousTokens, await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().OrderBy(token => token.Id).Select(token => new { token.Id, token.Status }).ToListAsync());
        }
        Assert.DoesNotContain(log.Lines(), line => line.Contains("added a claim to") || line.Contains("removed a claim from"));
    }
}
