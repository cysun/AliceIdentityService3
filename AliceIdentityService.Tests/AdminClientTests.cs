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
using Microsoft.IdentityModel.JsonWebTokens;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Tests;

[Collection("PostgreSQL")]
public sealed class AdminClientTests
{
    private const string Secret = "Test-Only-Client-Secret-0123456789-abcdefgh";
    private const string NextSecret = "Replacement-Client-Secret-9876543210-abcdefgh";
    private static async Task Login(HttpClient browser)
    {
        var html = await browser.GetStringAsync("/Account/Login");
        Assert.Equal(HttpStatusCode.Redirect, (await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = OidcTestClient.Email,
            ["Password"] = OidcTestClient.Password,
            ["__RequestVerificationToken"] = OidcTestClient.Field(html, "__RequestVerificationToken")
        }))).StatusCode);
    }
    private static async Task<HttpResponseMessage> Write(HttpClient browser, string path, object body, bool delete = false, string? csrf = null)
    {
        using var message = new HttpRequestMessage(delete ? HttpMethod.Delete : HttpMethod.Post, "/api/admin/clients" + path) { Content = JsonContent.Create(body) };
        if (csrf is null) csrf = (await browser.GetFromJsonAsync<JsonElement>("/api/admin/antiforgery")).GetProperty("requestToken").GetString();
        if (csrf != "missing") message.Headers.Add("X-CSRF-TOKEN", csrf);
        return await browser.SendAsync(message);
    }
    private static async Task Ok(HttpResponseMessage response) => Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
    private static AdminCreateClientInput Input(string id = "created-client", bool confidential = false) => new()
    {
        ClientId = id,
        DisplayName = "Created <client>",
        ClientType = confidential ? ClientTypes.Confidential : ClientTypes.Public,
        ClientSecret = confidential ? Secret : null,
        RedirectUris = [OidcTestClient.Callback],
        PostLogoutRedirectUris = [OidcTestClient.SignedOut],
        Scopes = ["email", "profile", "department"],
        AllowRefreshTokens = true,
        RequirePkce = true
    };
    private static async Task<AdminClientDetail> Create(HttpClient browser, AdminCreateClientInput input)
    {
        var response = await Write(browser, "", input); await Ok(response);
        Assert.DoesNotContain(Secret, await response.Content.ReadAsStringAsync());
        return await Detail(browser, (await response.Content.ReadFromJsonAsync<AdminMutationResult>())!.Id);
    }
    private static async Task<AdminClientDetail> Detail(HttpClient browser, string id) =>
        (await browser.GetFromJsonAsync<AdminClientDetail>("/api/admin/clients/" + id))!;
    private static AdminEditClientInput Edit(AdminClientDetail data) => new()
    {
        ClientId = data.Client.ClientId!,
        DisplayName = data.Client.DisplayName!,
        Version = data.Version,
        RedirectUris = data.RedirectUris.ToArray(),
        PostLogoutRedirectUris = data.PostLogoutRedirectUris.ToArray(),
        Scopes = data.Client.Scopes.ToArray(),
        AllowRefreshTokens = data.AllowRefreshTokens,
        RequirePkce = data.Client.RequirePkce
    };
    private static async Task<AdminClientDetail> Existing(OidcTestClient client, string clientId = OidcTestClient.ClientId)
    {
        var record = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(app => app.ClientId == clientId);
        return await Detail(client.Browser, record.Id!);
    }
    private static Task<HttpResponseMessage> Refresh(OidcTestClient client, string clientId, string token, string? secret = null) => client.Browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token",
        ["client_id"] = clientId,
        ["refresh_token"] = token,
        ["client_secret"] = secret ?? ""
    }));
    private static async Task<JsonElement> Issue(OidcTestClient client, string clientId, string? secret = null, bool pkce = true)
    {
        var parameters = client.Parameters(); parameters["client_id"] = clientId;
        if (!pkce) { parameters.Remove("code_challenge"); parameters.Remove("code_challenge_method"); }
        var code = await client.Code(parameters);
        var response = await client.Browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = OidcTestClient.Callback,
            ["client_id"] = clientId,
            ["client_secret"] = secret ?? "",
            ["code_verifier"] = pkce ? client.Verifier : ""
        }));
        await Ok(response); return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    [PostgresFact]
    public async Task ClientReadsAndEveryWriteRequireAdministratorAndAntiforgery()
    {
        await using var client = await OidcTestClient.Create();
        foreach (var path in new[] { "", "/missing" }) Assert.Equal(HttpStatusCode.Unauthorized, (await client.Browser.GetAsync("/api/admin/clients" + path)).StatusCode);
        foreach (var (path, delete) in new[] { ("", false), ("/missing", false), ("/missing/credentials", false), ("/missing", true) })
            Assert.Equal(HttpStatusCode.Unauthorized, (await Write(client.Browser, path, new { }, delete, "missing")).StatusCode);
        await Login(client.Browser);
        foreach (var (path, delete) in new[] { ("", false), ("/missing", false), ("/missing/credentials", false), ("/missing", true) })
            foreach (var csrf in new[] { "missing", "forged" }) Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, path, new { }, delete, csrf)).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.Browser.GetAsync("/api/admin/clients/missing/credentials")).StatusCode);
        Assert.True((await client.Browser.GetAsync("/api/admin/clients")).Headers.CacheControl!.NoStore);
        var user = await client.Database.Db.Users.SingleAsync(user => user.Email == OidcTestClient.Email);
        await client.Database.Db.UserClaims.Where(claim => claim.UserId == user.Id && claim.ClaimType == AisConstants.AdminClaim).ExecuteDeleteAsync();
        foreach (var path in new[] { "", "/missing" }) Assert.Equal(HttpStatusCode.Forbidden, (await client.Browser.GetAsync("/api/admin/clients" + path)).StatusCode);
        foreach (var (path, delete) in new[] { ("", false), ("/missing", false), ("/missing/credentials", false), ("/missing", true) })
            Assert.Equal(HttpStatusCode.Forbidden, (await Write(client.Browser, path, new { }, delete, "missing")).StatusCode);
    }

    [PostgresFact]
    public async Task CreationValidatesUrlsScopesCredentialsAndDuplicatesWithoutLeakingSecrets()
    {
        using var log = new TestLogFile();
        await using var client = await OidcTestClient.Create(log: log); await Login(client.Browser);
        foreach (var url in new[] { "relative", "http://remote.example/callback", "https://user:pass@example.test/path", "https://example.test/path#fragment", "https://*.example.test/callback", "javascript:alert(1)", "my-app:/callback", "https://example.test/\ncallback", "http://localhost.example.test/cb", null! })
        {
            var invalid = Input(); invalid.RedirectUris = [url];
            Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "", invalid)).StatusCode);
        }
        foreach (var invalid in new[] { Input(), Input(), Input(), Input(), Input(), Input(), Input(), Input(), Input(), Input() }.Select((input, index) =>
        {
            switch (index)
            {
                case 0: input.ClientId = "bad id"; break;
                case 1: input.RequirePkce = false; break;
                case 2: input.Scopes = ["unknown"]; break;
                case 3: input.Scopes = ["email", "email"]; break;
                case 4: input.RedirectUris = []; break;
                case 5: input.RedirectUris = [OidcTestClient.Callback, OidcTestClient.Callback]; break;
                case 6: input.ClientSecret = Secret; break;
                case 7: input.ClientType = ClientTypes.Confidential; input.ClientSecret = "weak"; break;
                case 8: input.ClientType = ClientTypes.Confidential; input.ClientSecret = null; break;
                case 9: input.PostLogoutRedirectUris = ["https://example.test/#bad"]; break;
            }
            return input;
        })) Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "", invalid)).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "", new { clientId = "missing-flags", displayName = "test", redirectUris = new[] { OidcTestClient.Callback }, postLogoutRedirectUris = Array.Empty<string>(), scopes = Array.Empty<string>() })).StatusCode);
        var input = Input(confidential: true);
        input.RedirectUris = [OidcTestClient.Callback + "?parameter=PRIVATE-CALLBACK"];
        var data = await Create(client.Browser, input);
        Assert.True(data.Client.HasSecret); Assert.True(data.Client.RequirePkce); Assert.True(data.CanManage);
        var record = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(app => app.Id == data.Client.Id);
        Assert.NotEqual(Secret, record.ClientSecret); Assert.NotNull(record.ClientSecret);
        foreach (var path in new[] { "", "/" + data.Client.Id })
        {
            var json = await client.Browser.GetStringAsync("/api/admin/clients" + path);
            Assert.DoesNotContain(Secret, json); Assert.DoesNotContain(record.ClientSecret!, json); Assert.DoesNotContain(record.ConcurrencyToken!, json);
            Assert.DoesNotContain("clientSecret", json); Assert.DoesNotContain("properties", json);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "", Input())).StatusCode);
        var loopback = Input("loopback"); loopback.RedirectUris = ["http://127.0.0.1:1234/callback", "http://[::1]:1234/callback", "http://localhost:1234/callback"];
        await Create(client.Browser, loopback);
        Assert.Empty((await client.Browser.GetFromJsonAsync<AdminClientPage>("/api/admin/clients?q=%25"))!.Items);
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.GetAsync("/api/admin/clients?pageSize=101")).StatusCode);
        var page = (await client.Browser.GetFromJsonAsync<AdminClientPage>("/api/admin/clients?pageSize=1&page=999"))!;
        Assert.Single(page.Items); Assert.Equal(page.TotalCount, page.Page);
        var lines = log.Lines();
        var created = Assert.Single(lines, line => line.Contains("created client created-client"));
        Assert.Contains(OidcTestClient.Email, created);
        Assert.Contains(data.Client.Id, created);
        Assert.Contains(OidcTestClient.Callback, created);
        Assert.Contains("department", created);
        Assert.DoesNotContain(lines, line => line.Contains(Secret) || line.Contains(record.ClientSecret!) || line.Contains("PRIVATE-CALLBACK"));
    }

    [PostgresFact]
    public async Task RegisteredPublicAndConfidentialClientsCompleteCodeRefreshAndUserInfoWithPkceEnforcement()
    {
        await using var client = await OidcTestClient.Create(); await Login(client.Browser);
        foreach (var confidential in new[] { false, true })
        {
            var input = Input("registered-" + confidential, confidential);
            await Create(client.Browser, input);
            var parameters = client.Parameters(); parameters["client_id"] = input.ClientId; parameters.Remove("code_challenge"); parameters.Remove("code_challenge_method");
            Assert.Equal(Errors.InvalidRequest, await OidcTestClient.Error(await client.Authorize(parameters)));
            parameters = client.Parameters(); parameters["client_id"] = input.ClientId; parameters["redirect_uri"] = "https://unregistered.example/cb";
            Assert.Equal(HttpStatusCode.BadRequest, (await client.Authorize(parameters)).StatusCode);
            var issued = await Issue(client, input.ClientId, confidential ? Secret : null);
            var identity = await client.ValidateIdToken(issued.GetProperty("id_token").GetString()!, input.ClientId);
            Assert.Equal("Computing", identity.GetClaim("department").Value);
            var access = new JsonWebToken(issued.GetProperty("access_token").GetString());
            Assert.Contains("department-api", access.Audiences);
            await Ok(await client.UserInfo(issued.GetProperty("access_token").GetString()!));
            if (confidential) Assert.Equal(Errors.InvalidClient, await OidcTestClient.Error(await Refresh(client, input.ClientId, issued.GetProperty("refresh_token").GetString()!, "wrong")));
            await Ok(await Refresh(client, input.ClientId, issued.GetProperty("refresh_token").GetString()!, confidential ? Secret : null));
        }
        var compatible = Input("confidential-without-pkce", true); compatible.RequirePkce = false;
        await Create(client.Browser, compatible); await Issue(client, compatible.ClientId, Secret, pkce: false);
    }

    [PostgresFact]
    public async Task DisplayNameEditPreservesSecretImportedMetadataCallbacksAndGrantsAndRejectsStaleUpdates()
    {
        await using var client = await OidcTestClient.Create(); await Login(client.Browser);
        var data = await Create(client.Browser, Input(confidential: true));
        var issued = await Issue(client, data.Client.ClientId!, Secret);
        await using (var services = client.Factory.Services.CreateAsyncScope())
        {
            var apps = services.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var record = (await apps.FindByIdAsync(data.Client.Id))!; var descriptor = new OpenIddictApplicationDescriptor(); await apps.PopulateAsync(descriptor, record);
            descriptor.Properties["preserved"] = JsonSerializer.SerializeToElement(new { secret = "PRIVATE-TEST-METADATA" });
            descriptor.DisplayNames[System.Globalization.CultureInfo.GetCultureInfo("fr")] = "Application";
            descriptor.Settings["custom-setting"] = "PRIVATE-TEST-SETTING";
            descriptor.Permissions.Add("custom-permission"); descriptor.Requirements.Add("custom-requirement");
            descriptor.RedirectUris.Add(new Uri("http://legacy.example/callback"));
            await apps.UpdateAsync(record, descriptor);
        }
        var before = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(app => app.Id == data.Client.Id);
        data = await Detail(client.Browser, data.Client.Id); var input = Edit(data); input.DisplayName = " Renamed ";
        var fields = JsonSerializer.SerializeToElement(input).EnumerateObject().ToDictionary(field => field.Name, field => (object)field.Value);
        fields["clientSecret"] = "injected"; fields["clientType"] = "public"; fields["consentType"] = "explicit"; fields["permissions"] = new[] { "injected" };
        await Ok(await Write(client.Browser, "/" + data.Client.Id, fields));
        var after = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(app => app.Id == data.Client.Id);
        Assert.Equal("Renamed", after.DisplayName); Assert.Equal(before.ClientSecret, after.ClientSecret); Assert.Equal(before.ClientType, after.ClientType);
        Assert.Equal(before.Properties, after.Properties); Assert.Equal(before.Settings, after.Settings); Assert.Equal(before.DisplayNames, after.DisplayNames);
        Assert.Equal(JsonSerializer.Deserialize<string[]>(before.Permissions!)!.Order(), JsonSerializer.Deserialize<string[]>(after.Permissions!)!.Order()); Assert.Equal(before.Requirements, after.Requirements); Assert.Equal(before.RedirectUris, after.RedirectUris);
        Assert.DoesNotContain("PRIVATE-", await client.Browser.GetStringAsync("/api/admin/clients/" + data.Client.Id));
        await Ok(await Refresh(client, data.Client.ClientId!, issued.GetProperty("refresh_token").GetString()!, Secret));
        Assert.Equal(HttpStatusCode.Conflict, (await Write(client.Browser, "/" + data.Client.Id, input)).StatusCode);
        input = Edit(await Detail(client.Browser, data.Client.Id)); input.ClientId = "renamed-id";
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + data.Client.Id, input)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await Write(client.Browser, "/missing", input)).StatusCode);
    }

    [PostgresFact]
    public async Task SecuritySettingsRevokeOnlyThisClientsCodesAndRefreshTokensAndLeaveUserCookiesValid()
    {
        foreach (var operation in new[] { "redirect", "logout", "scopes", "refresh", "pkce" })
        {
            await using var client = await OidcTestClient.Create(); await Login(client.Browser);
            var input = Input(confidential: true); var data = await Create(client.Browser, input);
            var issued = await Issue(client, input.ClientId, Secret); var parameters = client.Parameters(); parameters["client_id"] = input.ClientId;
            var code = await client.Code(parameters); var other = await client.Tokens();
            var edit = Edit(await Detail(client.Browser, data.Client.Id));
            switch (operation)
            {
                case "redirect": edit.RedirectUris = ["https://new.example/callback"]; break;
                case "logout": edit.PostLogoutRedirectUris = []; break;
                case "scopes": edit.Scopes = ["email", "profile"]; break;
                case "refresh": edit.AllowRefreshTokens = false; break;
                case "pkce": edit.RequirePkce = false; break;
            }
            await Ok(await Write(client.Browser, "/" + data.Client.Id, edit));
            Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Redeem(code, clientId: input.ClientId, secret: Secret)));
            Assert.False((await Refresh(client, input.ClientId, issued.GetProperty("refresh_token").GetString()!, Secret)).IsSuccessStatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.Browser.GetAsync("/api/admin/session")).StatusCode);
            await Ok(await client.Refresh(other.GetProperty("refresh_token").GetString()!));
            Assert.All(await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().Where(token => token.Application!.Id == data.Client.Id).ToListAsync(), token => Assert.Equal(Statuses.Revoked, token.Status));
            Assert.All(await client.Database.Db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking().Where(auth => auth.Application!.Id == data.Client.Id).ToListAsync(), auth => Assert.Equal(Statuses.Revoked, auth.Status));
            if (operation == "scopes") Assert.Equal(Errors.InvalidRequest, await OidcTestClient.Error(await client.Authorize(parameters)));
        }
    }

    [PostgresFact]
    public async Task CredentialRotationAndTypeConversionReplaceHashesAndInvalidateOldGrants()
    {
        await using var client = await OidcTestClient.Create(); await Login(client.Browser);
        var data = await Create(client.Browser, Input(confidential: true));
        var issued = await Issue(client, data.Client.ClientId!, Secret);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + data.Client.Id + "/credentials", new { data.Version, clientType = "confidential", clientSecret = NextSecret, confirmation = "wrong" })).StatusCode);
        await Ok(await Write(client.Browser, "/" + data.Client.Id + "/credentials", new { data.Version, clientType = "confidential", clientSecret = NextSecret, confirmation = data.Client.ClientId }));
        Assert.Equal(Errors.InvalidClient, await OidcTestClient.Error(await Refresh(client, data.Client.ClientId!, issued.GetProperty("refresh_token").GetString()!, Secret)));
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await Refresh(client, data.Client.ClientId!, issued.GetProperty("refresh_token").GetString()!, NextSecret)));
        await Issue(client, data.Client.ClientId!, NextSecret);
        data = await Detail(client.Browser, data.Client.Id);
        await Ok(await Write(client.Browser, "/" + data.Client.Id + "/credentials", new { data.Version, clientType = "public", confirmation = data.Client.ClientId }));
        data = await Detail(client.Browser, data.Client.Id); Assert.False(data.Client.HasSecret); Assert.True(data.Client.RequirePkce); Assert.Equal("public", data.Client.ClientType);
        await Issue(client, data.Client.ClientId!);
        await Ok(await Write(client.Browser, "/" + data.Client.Id + "/credentials", new { data.Version, clientType = "confidential", clientSecret = Secret, confirmation = data.Client.ClientId }));
        await Issue(client, data.Client.ClientId!, Secret);
    }

    [PostgresFact]
    public async Task DeletionRemovesProtocolRowsAndOldGrantsStayInvalidAfterIdIsReused()
    {
        await using var client = await OidcTestClient.Create(); await Login(client.Browser);
        var data = await Create(client.Browser, Input()); var issued = await Issue(client, data.Client.ClientId!);
        var parameters = client.Parameters(); parameters["client_id"] = data.Client.ClientId; var code = await client.Code(parameters);
        data = await Detail(client.Browser, data.Client.Id);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + data.Client.Id, new { data.Version, confirmation = "wrong" }, true)).StatusCode);
        await Ok(await Write(client.Browser, "/" + data.Client.Id, new { data.Version, confirmation = data.Client.ClientId }, true));
        Assert.Equal(HttpStatusCode.NotFound, (await client.Browser.GetAsync("/api/admin/clients/" + data.Client.Id)).StatusCode);
        Assert.False(await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AnyAsync(token => token.Application!.Id == data.Client.Id));
        Assert.False(await client.Database.Db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AnyAsync(auth => auth.Application!.Id == data.Client.Id));
        await Create(client.Browser, Input());
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Redeem(code, clientId: data.Client.ClientId!)));
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await Refresh(client, data.Client.ClientId!, issued.GetProperty("refresh_token").GetString()!)));
    }

    [PostgresFact]
    public async Task RevocationFailureRollsBackSettingsSecretAndTokenChanges()
    {
        await using var client = await OidcTestClient.Create(); await Login(client.Browser);
        var data = await Create(client.Browser, Input(confidential: true)); await Issue(client, data.Client.ClientId!, Secret);
        data = await Detail(client.Browser, data.Client.Id);
        var original = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(app => app.Id == data.Client.Id);
        var previousTokens = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().OrderBy(token => token.Id).Select(token => new { token.Id, token.Status }).ToListAsync();
        await client.Database.Db.Database.ExecuteSqlRawAsync("""
            CREATE FUNCTION fail_client_revoke() RETURNS trigger LANGUAGE plpgsql AS $$
            BEGIN RAISE EXCEPTION 'Deliberate test failure'; END; $$;
            CREATE TRIGGER fail_client_revoke BEFORE UPDATE ON "OpenIddictAuthorizations" FOR EACH ROW EXECUTE FUNCTION fail_client_revoke();
            """);
        var edit = Edit(data); edit.Scopes = ["email"];
        await Assert.ThrowsAsync<PostgresException>(() => Write(client.Browser, "/" + data.Client.Id, edit));
        await Assert.ThrowsAsync<PostgresException>(() => Write(client.Browser, "/" + data.Client.Id + "/credentials", new { data.Version, clientType = "confidential", clientSecret = NextSecret, confirmation = data.Client.ClientId }));
        await client.Database.Db.Database.ExecuteSqlRawAsync("""
            CREATE TRIGGER fail_client_delete BEFORE DELETE ON "OpenIddictApplications" FOR EACH ROW EXECUTE FUNCTION fail_client_revoke();
            """);
        var deletion = await Record.ExceptionAsync(() => Write(client.Browser, "/" + data.Client.Id, new { data.Version, confirmation = data.Client.ClientId }, true));
        Assert.NotNull(deletion);
        var postgres = deletion as PostgresException ?? deletion.InnerException as PostgresException;
        Assert.Equal("P0001", postgres?.SqlState);
        var unchanged = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking().SingleAsync(app => app.Id == data.Client.Id);
        Assert.Equal(original.Permissions, unchanged.Permissions); Assert.Equal(original.ClientSecret, unchanged.ClientSecret); Assert.Equal(original.ConcurrencyToken, unchanged.ConcurrencyToken);
        Assert.Equal(previousTokens, await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().OrderBy(token => token.Id).Select(token => new { token.Id, token.Status }).ToListAsync());
    }

    [PostgresFact]
    public async Task ConcurrentEditsRejectStaleVersionsAndActorPermissionIsRecheckedInsideTheTransaction()
    {
        await using var client = await OidcTestClient.Create(); await Login(client.Browser);
        var data = await Create(client.Browser, Input());
        var csrf = (await client.Browser.GetFromJsonAsync<JsonElement>("/api/admin/antiforgery")).GetProperty("requestToken").GetString()!;
        var results = await Task.WhenAll(Write(client.Browser, "/" + data.Client.Id, Edit(data), csrf: csrf), Write(client.Browser, "/" + data.Client.Id, Edit(data), csrf: csrf));
        Assert.Single(results, response => response.StatusCode == HttpStatusCode.OK); Assert.Single(results, response => response.StatusCode == HttpStatusCode.Conflict);
        await using var services = client.Factory.Services.CreateAsyncScope();
        var users = services.ServiceProvider.GetRequiredService<UserManager<User>>(); var user = (await users.FindByEmailAsync(OidcTestClient.Email))!;
        var principal = await services.ServiceProvider.GetRequiredService<SignInManager<User>>().CreateUserPrincipalAsync(user);
        await client.Database.Db.UserClaims.Where(claim => claim.UserId == user.Id && claim.ClaimType == AisConstants.AdminClaim).ExecuteDeleteAsync();
        Assert.Equal(403, (await services.ServiceProvider.GetRequiredService<AdminClientService>().CreateAsync(principal, Input("denied"), CancellationToken.None)).Status);
        Assert.False(await client.Database.Db.Set<OpenIddictEntityFrameworkCoreApplication>().AnyAsync(app => app.ClientId == "denied"));
    }

    [PostgresFact]
    public async Task ChangedRedirectOriginsAndCredentialsAreObservedAcrossInstancesAndScopesCanBeDeletedAfterRemoval()
    {
        await using var client = await OidcTestClient.Create(); await Login(client.Browser);
        var data = await Create(client.Browser, Input(confidential: true));
        await using var other = new AuthenticationFactory(client.Database.Db.Database.GetConnectionString()!);
        using var browser = other.Browser();
        async Task<HttpResponseMessage> Cors(string origin)
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, "/connect/token"); request.Headers.Add("Origin", origin); request.Headers.Add("Access-Control-Request-Method", "POST");
            return await browser.SendAsync(request);
        }
        // Use a unique origin so no other client's redirects can allow it.
        var edit = Edit(data); edit.RedirectUris = ["https://only-this-client.example/cb"];
        await Ok(await Write(client.Browser, "/" + data.Client.Id, edit));
        Assert.True((await Cors("https://only-this-client.example")).Headers.Contains("Access-Control-Allow-Origin"));
        data = await Detail(client.Browser, data.Client.Id); edit = Edit(data); edit.RedirectUris = ["https://replacement.example/cb"];
        await Ok(await Write(client.Browser, "/" + data.Client.Id, edit));
        Assert.False((await Cors("https://only-this-client.example")).Headers.Contains("Access-Control-Allow-Origin"));
        Assert.True((await Cors("https://replacement.example")).Headers.Contains("Access-Control-Allow-Origin"));
        async Task<bool> ValidateSecret(string secret)
        {
            await using var services = other.Services.CreateAsyncScope(); var apps = services.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            return await apps.ValidateClientSecretAsync((await apps.FindByClientIdAsync(data.Client.ClientId!))!, secret);
        }
        Assert.True(await ValidateSecret(Secret));
        data = await Detail(client.Browser, data.Client.Id);
        await Ok(await Write(client.Browser, "/" + data.Client.Id + "/credentials", new { data.Version, clientType = "confidential", clientSecret = NextSecret, confirmation = data.Client.ClientId }));
        Assert.False(await ValidateSecret(Secret)); Assert.True(await ValidateSecret(NextSecret));
        var scopeManager = client.Database.Services.GetRequiredService<IOpenIddictScopeManager>();
        var custom = await scopeManager.CreateAsync(new OpenIddictScopeDescriptor { Name = "removable" });
        data = await Detail(client.Browser, data.Client.Id); edit = Edit(data); edit.Scopes = ["removable"];
        await Ok(await Write(client.Browser, "/" + data.Client.Id, edit));
        var scopeId = await scopeManager.GetIdAsync(custom);
        var definition = (await client.Browser.GetFromJsonAsync<AdminScopeDetail>("/api/admin/scopes/" + scopeId))!;
        async Task<HttpResponseMessage> DeleteScope()
        {
            using var request = new HttpRequestMessage(HttpMethod.Delete, "/api/admin/scopes/" + scopeId) { Content = JsonContent.Create(new { definition.Version, confirmation = "removable" }) };
            request.Headers.Add("X-CSRF-TOKEN", (await client.Browser.GetFromJsonAsync<JsonElement>("/api/admin/antiforgery")).GetProperty("requestToken").GetString());
            return await client.Browser.SendAsync(request);
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await DeleteScope()).StatusCode);
        data = await Detail(client.Browser, data.Client.Id); edit = Edit(data); edit.Scopes = [];
        await Ok(await Write(client.Browser, "/" + data.Client.Id, edit)); await Ok(await DeleteScope());
    }
    [PostgresFact]
    public async Task ImportedNativeCallbacksAreRetainedAndUnsupportedFlowsAreReadOnly()
    {
        await using var client = await OidcTestClient.Create(); await Login(client.Browser);
        var data = await Create(client.Browser, Input());
        await using (var services = client.Factory.Services.CreateAsyncScope())
        {
            var apps = services.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var record = (await apps.FindByIdAsync(data.Client.Id))!;
            var descriptor = new OpenIddictApplicationDescriptor(); await apps.PopulateAsync(descriptor, record);
            descriptor.ApplicationType = ApplicationTypes.Native;
            descriptor.RedirectUris.Add(new Uri("com.example.native:/callback"));
            descriptor.Requirements.Remove(Requirements.Features.ProofKeyForCodeExchange);
            await apps.UpdateAsync(record, descriptor);
        }
        data = await Detail(client.Browser, data.Client.Id);
        var edit = Edit(data); edit.DisplayName = "Imported native app";
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + data.Client.Id, edit)).StatusCode);
        edit.RequirePkce = true; await Ok(await Write(client.Browser, "/" + data.Client.Id, edit));
        data = await Detail(client.Browser, data.Client.Id);
        Assert.Contains("com.example.native:/callback", data.RedirectUris); Assert.Equal(ApplicationTypes.Native, data.ApplicationType);
        await using (var services = client.Factory.Services.CreateAsyncScope())
        {
            var apps = services.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
            var record = (await apps.FindByIdAsync(data.Client.Id))!;
            var descriptor = new OpenIddictApplicationDescriptor(); await apps.PopulateAsync(descriptor, record);
            descriptor.ConsentType = ConsentTypes.Explicit; await apps.UpdateAsync(record, descriptor);
        }
        data = await Detail(client.Browser, data.Client.Id); Assert.False(data.CanManage);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + data.Client.Id, Edit(data))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await Write(client.Browser, "/" + data.Client.Id + "/credentials", new { data.Version, clientType = "confidential", clientSecret = Secret, confirmation = data.Client.ClientId })).StatusCode);
    }

}
