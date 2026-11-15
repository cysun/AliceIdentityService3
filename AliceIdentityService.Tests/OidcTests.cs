using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Tests;

internal sealed class OidcTestClient(PostgresTestDatabase database, AuthenticationFactory factory) : IAsyncDisposable
{
    public const string Email = "oidc@example.test";
    public const string Password = "Oidc-Test-Password-42!";
    public const string ClientId = "public-test-client";
    public const string Callback = "https://client.example.test/callback";
    public const string SignedOut = "https://client.example.test/signed-out";
    public const string Secret = "Test-only-client-secret-42!";
    public PostgresTestDatabase Database { get; } = database;
    public AuthenticationFactory Factory { get; } = factory;
    public HttpClient Browser { get; } = factory.Browser();
    public string Verifier { get; } = Base64UrlEncoder.Encode(RandomNumberGenerator.GetBytes(32));
    public string State { get; } = Guid.NewGuid().ToString("N");
    public string Nonce { get; } = Guid.NewGuid().ToString("N");

    public static async Task<OidcTestClient> Create(Action<IServiceCollection>? configure = null)
    {
        var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        await database.Services.GetRequiredService<BootstrapService>().RunAsync(new(Email, "OIDC", "User"), () => Task.FromResult(Password));
        var scopes = database.Services.GetRequiredService<IOpenIddictScopeManager>();
        var scope = new OpenIddictScopeDescriptor { Name = "department" };
        scope.Resources.Add("department-api");
        scope.Properties["claims"] = JsonSerializer.SerializeToElement(new[] { "department", "AspNet.Identity.SecurityStamp", "oi_scopes" });
        await scopes.CreateAsync(scope);
        var users = database.Services.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByEmailAsync(Email))!;
        Assert.True((await users.AddClaimAsync(user, new Claim("department", "Computing"))).Succeeded);
        var applications = database.Services.GetRequiredService<IOpenIddictApplicationManager>();
        foreach (var confidential in new[] { false, true })
        {
            var application = new OpenIddictApplicationDescriptor
            {
                ClientId = confidential ? "confidential-test-client" : ClientId,
                ClientSecret = confidential ? Secret : null,
                ClientType = confidential ? ClientTypes.Confidential : ClientTypes.Public,
                DisplayName = "Review Portal <test>",
                ConsentType = ConsentTypes.Implicit,
                RedirectUris = { new Uri(Callback) },
                PostLogoutRedirectUris = { new Uri(SignedOut) },
                // Intentionally no endpoint permissions, matching AIS2-created clients.
                Permissions = { Permissions.GrantTypes.AuthorizationCode, Permissions.GrantTypes.RefreshToken,
                    Permissions.ResponseTypes.Code, Permissions.Scopes.Profile, Permissions.Scopes.Email, Permissions.Prefixes.Scope + "department" }
            };
            if (!confidential) application.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
            await applications.CreateAsync(application);
        }
        return new(database, new AuthenticationFactory(database.Db.Database.GetConnectionString()!, configure));
    }

    public Dictionary<string, string?> Parameters() => new()
    {
        ["client_id"] = ClientId,
        ["redirect_uri"] = Callback,
        ["response_type"] = "code",
        ["scope"] = "openid profile email department offline_access",
        ["state"] = State,
        ["nonce"] = Nonce,
        ["code_challenge"] = Base64UrlEncoder.Encode(SHA256.HashData(Encoding.ASCII.GetBytes(Verifier))),
        ["code_challenge_method"] = "S256"
    };

    public Task<HttpResponseMessage> Authorize(Dictionary<string, string?>? parameters = null) =>
        Browser.GetAsync(QueryHelpers.AddQueryString("/connect/authorize", parameters ?? Parameters()));

    public static string Field(string html, string name) => WebUtility.HtmlDecode(Regex.Match(html,
        $"name=\"{Regex.Escape(name)}\"[^>]*value=\"([^\"]*)\"").Groups[1].Value);

    public async Task<HttpResponseMessage> SubmitLogin(string html, string password = Password, string? contextOverride = null)
    {
        return await Browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = Email,
            ["Password"] = password,
            ["OidcContext"] = contextOverride ?? Field(html, "OidcContext"),
            ["__RequestVerificationToken"] = Field(html, "__RequestVerificationToken"),
            ["ReturnUrl"] = "https://evil.example/ignored"
        }));
    }

    public async Task<string> Code(Dictionary<string, string?>? parameters = null)
    {
        var response = await Authorize(parameters);
        if (response.StatusCode == HttpStatusCode.OK)
        {
            response = await SubmitLogin(await response.Content.ReadAsStringAsync());
            Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
            response = await Browser.GetAsync(response.Headers.Location);
        }
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.StartsWith(Callback + "?", response.Headers.Location!.OriginalString);
        var query = QueryHelpers.ParseQuery(response.Headers.Location.Query);
        Assert.False(query.ContainsKey("error"), response.Headers.Location.OriginalString);
        Assert.Equal(State, query["state"]);
        Assert.NotEmpty(query["code"].ToString());
        return query["code"].ToString();
    }

    public Task<HttpResponseMessage> Redeem(string code, string? verifier = null, string clientId = ClientId, string? secret = null) =>
        Browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["client_id"] = clientId,
            ["redirect_uri"] = Callback,
            ["code_verifier"] = verifier ?? Verifier,
            ["client_secret"] = secret ?? ""
        }));

    public Task<HttpResponseMessage> Refresh(string token, string? scope = null) => Browser.PostAsync("/connect/token",
        new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["refresh_token"] = token,
            ["client_id"] = ClientId,
            ["scope"] = scope ?? ""
        }));

    public async Task<JsonElement> Tokens()
    {
        var response = await Redeem(await Code());
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    public async Task<JsonWebToken> ValidateIdToken(string encoded, string audience = ClientId)
    {
        var discovery = await Browser.GetFromJsonAsync<JsonElement>("/.well-known/openid-configuration");
        Assert.Equal("https://localhost/", discovery.GetProperty("issuer").GetString());
        var keys = new JsonWebKeySet(await Browser.GetStringAsync(discovery.GetProperty("jwks_uri").GetString()));
        var validation = await new JsonWebTokenHandler().ValidateTokenAsync(encoded, new TokenValidationParameters
        {
            ValidIssuer = "https://localhost/",
            ValidAudience = audience,
            IssuerSigningKeys = keys.GetSigningKeys(),
            ClockSkew = TimeSpan.Zero
        });
        Assert.True(validation.IsValid, validation.Exception?.ToString());
        return (JsonWebToken)validation.SecurityToken;
    }

    public async Task<HttpResponseMessage> UserInfo(string accessToken)
    {
        using var message = new HttpRequestMessage(HttpMethod.Get, "/connect/userinfo");
        message.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return await Browser.SendAsync(message);
    }

    public static async Task<string> Error(HttpResponseMessage response)
    {
        if (response.Headers.Location is { } location) return QueryHelpers.ParseQuery(location.Query)["error"].ToString();
        var body = await response.Content.ReadAsStringAsync();
        if (response.Content.Headers.ContentType?.MediaType == "application/json")
            return JsonDocument.Parse(body).RootElement.GetProperty("error").GetString()!;
        return Regex.Match(body, @"(?m)^error:\s*([^\r\n]+)").Groups[1].Value;
    }

    public async ValueTask DisposeAsync()
    {
        Browser.Dispose();
        await Factory.DisposeAsync();
        await Database.DisposeAsync();
    }
}

[Collection("PostgreSQL")]
public sealed class OidcTests
{
    [PostgresFact]
    public async Task BrowserTokenCorsAllowsRegisteredOriginsWithoutCookieCredentials()
    {
        await using var client = await OidcTestClient.Create();
        foreach (var (origin, path, allowed) in new[]
        {
            ("https://client.example.test", "/connect/token", true),
            ("https://client.example.test", "/connect/userinfo", true),
            ("https://evil.example", "/connect/token", false),
            ("https://client.example.test", "/api/system/info", false)
        })
        {
            using var request = new HttpRequestMessage(HttpMethod.Options, path);
            request.Headers.Add("Origin", origin);
            request.Headers.Add("Access-Control-Request-Method", "POST");
            request.Headers.Add("Access-Control-Request-Headers", "content-type,authorization");
            var response = await client.Browser.SendAsync(request);
            Assert.Equal(allowed, response.Headers.Contains("Access-Control-Allow-Origin"));
            Assert.False(response.Headers.Contains("Access-Control-Allow-Credentials"));
        }
    }

    [PostgresFact]
    public async Task PublicPkceClientCompletesLoginDiscoveryCodeExchangeRefreshAndUserInfo()
    {
        await using var client = await OidcTestClient.Create();
        var tokens = await client.Tokens();
        var idToken = await client.ValidateIdToken(tokens.GetProperty("id_token").GetString()!);
        Assert.Equal(client.Nonce, idToken.GetClaim("nonce").Value);
        Assert.Equal(OidcTestClient.Email, idToken.GetClaim("name").Value);
        Assert.Equal("OIDC", idToken.GetClaim("given_name").Value);
        Assert.Equal("User", idToken.GetClaim("family_name").Value);
        Assert.Equal("Computing", idToken.GetClaim("department").Value);
        Assert.True(idToken.TryGetClaim("auth_time", out _));
        Assert.False(idToken.TryGetClaim("AspNet.Identity.SecurityStamp", out _));
        Assert.False(idToken.TryGetClaim(AisConstants.AdminClaim, out _));
        var accessToken = new JsonWebToken(tokens.GetProperty("access_token").GetString());
        Assert.Contains("department-api", accessToken.Audiences);
        Assert.Equal("Computing", accessToken.GetClaim("department").Value);
        Assert.False(accessToken.TryGetClaim("given_name", out _));
        Assert.False(accessToken.TryGetClaim("AspNet.Identity.SecurityStamp", out _));
        var info = await client.UserInfo(tokens.GetProperty("access_token").GetString()!);
        Assert.Equal(HttpStatusCode.OK, info.StatusCode);
        var profile = await info.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(idToken.Subject, profile.GetProperty("sub").GetString());
        Assert.True(profile.GetProperty("email_verified").GetBoolean());

        var refreshed = await client.Refresh(tokens.GetProperty("refresh_token").GetString()!);
        Assert.True(refreshed.IsSuccessStatusCode, await refreshed.Content.ReadAsStringAsync());
        var renewed = await refreshed.Content.ReadFromJsonAsync<JsonElement>();
        Assert.NotEqual(tokens.GetProperty("refresh_token").GetString(), renewed.GetProperty("refresh_token").GetString());
        var refreshedId = await client.ValidateIdToken(renewed.GetProperty("id_token").GetString()!);
        Assert.Equal(idToken.GetClaim("auth_time").Value, refreshedId.GetClaim("auth_time").Value);
        var refreshEntries = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking()
            .Where(token => token.Type == TokenTypeIdentifiers.RefreshToken).ToListAsync();
        Assert.Equal(2, refreshEntries.Count);
        Assert.Single(refreshEntries.Select(token => token.ExpirationDate).Distinct());
    }

    [PostgresFact]
    public async Task CompactLoginUsesValidatedEscapedNameAndPreservesContextOnErrorsAndLinks()
    {
        await using var client = await OidcTestClient.Create();
        var html = await (await client.Authorize()).Content.ReadAsStringAsync();
        Assert.Contains("account-shell oidc", html);
        Assert.Contains("Review Portal &lt;test&gt;", html);
        Assert.DoesNotContain("One account.", html);
        Assert.Contains("action=\"/Account/Login\"", html);
        var context = OidcTestClient.Field(html, "OidcContext");
        Assert.NotEmpty(context);
        var failed = await client.SubmitLogin(html, "wrong");
        var failedHtml = await failed.Content.ReadAsStringAsync();
        Assert.Contains("account-shell oidc", failedHtml);
        Assert.Equal(context, OidcTestClient.Field(failedHtml, "OidcContext"));
        Assert.DoesNotContain("value=\"wrong\"", failedHtml);
        foreach (var page in new[] { "Register", "ForgotPassword" })
        {
            var pageHtml = await client.Browser.GetStringAsync("/Account/" + page + "?context=" + Uri.EscapeDataString(context));
            Assert.Contains("account-shell oidc", pageHtml);
            Assert.Contains("/Account/Login?context=", pageHtml);
            Assert.Contains("<form", pageHtml);
            Assert.Equal(context, OidcTestClient.Field(pageHtml, "OidcContext"));
            Assert.NotEmpty(OidcTestClient.Field(pageHtml, "__RequestVerificationToken"));
        }
        Assert.Equal(HttpStatusCode.BadRequest, (await client.SubmitLogin(failedHtml, contextOverride: context + "tampered")).StatusCode);
        var protector = client.Factory.Services.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("AIS3.OidcLoginContext.v1").ToTimeLimitedDataProtector();
        var expired = protector.Protect(JsonSerializer.Serialize(new ValidatedLoginContext(OidcTestClient.ClientId, "/connect/authorize")), DateTimeOffset.UtcNow.AddMinutes(-1));
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.GetAsync("/Account/Login?context=" + Uri.EscapeDataString(expired))).StatusCode);
    }

    [PostgresFact]
    public async Task InvalidClientsRedirectsScopesResponseTypesAndMissingPkceNeverReachLogin()
    {
        await using var client = await OidcTestClient.Create();
        foreach (var (parameter, value) in new (string, string?)[]
        {
            ("client_id", "unknown"), ("redirect_uri", "https://evil.example/callback"),
            ("scope", "openid unregistered"), ("scope", "openid phone"), ("response_type", "token"),
            ("code_challenge", null), ("code_challenge_method", "plain")
        })
        {
            var request = client.Parameters();
            request[parameter] = value;
            var response = await client.Authorize(request);
            Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
            Assert.NotEmpty(await OidcTestClient.Error(response));
            Assert.DoesNotContain("account-shell", await response.Content.ReadAsStringAsync());
            if (parameter is "client_id" or "redirect_uri") Assert.Null(response.Headers.Location);
        }
    }

    [PostgresFact]
    public async Task PkceVerifierClientBindingAndCodeReplayAreEnforced()
    {
        await using var client = await OidcTestClient.Create();
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Redeem(await client.Code(), verifier: new string('x', 43))));
        var code = await client.Code();
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Redeem(code, clientId: "confidential-test-client", secret: OidcTestClient.Secret)));
        code = await client.Code();
        Assert.Equal(HttpStatusCode.OK, (await client.Redeem(code)).StatusCode);
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Redeem(code)));
    }

    [PostgresFact]
    public async Task ConfidentialLegacyClientWithoutPkceRequiresItsSecret()
    {
        await using var client = await OidcTestClient.Create();
        var request = client.Parameters();
        request["client_id"] = "confidential-test-client";
        request.Remove("code_challenge"); request.Remove("code_challenge_method");
        var code = await client.Code(request);
        Assert.Equal(Errors.InvalidClient, await OidcTestClient.Error(await client.Redeem(code, verifier: "", clientId: "confidential-test-client", secret: "wrong")));
        var response = await client.Redeem(code, verifier: "", clientId: "confidential-test-client", secret: OidcTestClient.Secret);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>();
        await client.ValidateIdToken(tokens.GetProperty("id_token").GetString()!, "confidential-test-client");
    }

    [PostgresFact]
    public async Task PromptNoneAndForcedLoginRespectTheActualSessionWithoutASkipFlag()
    {
        await using var client = await OidcTestClient.Create();
        var request = client.Parameters(); request["prompt"] = "none";
        Assert.Equal(Errors.LoginRequired, await OidcTestClient.Error(await client.Authorize(request)));
        await client.Tokens();
        Assert.Equal(HttpStatusCode.Redirect, (await client.Authorize(request)).StatusCode);
        foreach (var (key, value) in new[] { ("prompt", "login"), ("prompt", "select_account"), ("max_age", "0"), ("max_age", "00") })
        {
            request = client.Parameters(); request[key] = value;
            Assert.Equal(HttpStatusCode.OK, (await client.Authorize(request)).StatusCode);
            Assert.Equal(HttpStatusCode.OK, (await client.Authorize(request)).StatusCode);
            Assert.NotEmpty(await client.Code(request));
        }
        request = client.Parameters(); request["max_age"] = long.MaxValue.ToString();
        Assert.Equal(HttpStatusCode.Redirect, (await client.Authorize(request)).StatusCode);
    }

    [PostgresFact]
    public async Task RefreshUsesCurrentClaimsAndRejectsReplayAndScopeEscalation()
    {
        await using var client = await OidcTestClient.Create();
        var tokens = await client.Tokens();
        client.Database.Db.ChangeTracker.Clear();
        var user = await client.Database.Db.Users.SingleAsync();
        user.FirstName = "Updated";
        var department = await client.Database.Db.UserClaims.SingleAsync(claim => claim.ClaimType == "department");
        client.Database.Db.UserClaims.Remove(department);
        await client.Database.Db.SaveChangesAsync();
        var refresh = tokens.GetProperty("refresh_token").GetString()!;
        Assert.Contains(await OidcTestClient.Error(await client.Refresh(refresh, "openid phone")), new[] { Errors.InvalidScope, Errors.InvalidRequest });
        var response = await client.Refresh(refresh);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var renewed = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = await client.ValidateIdToken(renewed.GetProperty("id_token").GetString()!);
        Assert.Equal("Updated", id.GetClaim("given_name").Value);
        Assert.False(id.TryGetClaim("department", out _));
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Refresh(refresh)));
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Refresh(renewed.GetProperty("refresh_token").GetString()!)));
    }

    [PostgresFact]
    public async Task AccountChangesInvalidateIssuedCodesAndRefreshTokens()
    {
        await using var client = await OidcTestClient.Create();
        foreach (var state in new[] { "stamp", "locked", "unconfirmed", "two-factor", "deleted" })
        {
            var tokens = await client.Tokens();
            var code = await client.Code();
            client.Database.Db.ChangeTracker.Clear();
            var user = await client.Database.Db.Users.SingleAsync();
            switch (state)
            {
                case "stamp": user.SecurityStamp = Guid.NewGuid().ToString(); break;
                case "locked": user.LockoutEnd = DateTimeOffset.UtcNow.AddHours(1); break;
                case "unconfirmed": user.EmailConfirmed = false; break;
                case "two-factor": user.TwoFactorEnabled = true; break;
                case "deleted": client.Database.Db.Remove(user); break;
            }
            await client.Database.Db.SaveChangesAsync();
            Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Redeem(code)));
            Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Refresh(tokens.GetProperty("refresh_token").GetString()!)));
            if (state != "deleted")
            {
                user.LockoutEnd = null; user.EmailConfirmed = true; user.TwoFactorEnabled = false;
                await client.Database.Db.SaveChangesAsync();
            }
        }
    }

    [PostgresFact]
    public async Task LogoutValidatesRedirectAndRequiresAntiforgeryConfirmation()
    {
        await using var client = await OidcTestClient.Create();
        var tokens = await client.Tokens();
        var parameters = new Dictionary<string, string?>
        {
            ["id_token_hint"] = tokens.GetProperty("id_token").GetString(),
            ["client_id"] = OidcTestClient.ClientId,
            ["post_logout_redirect_uri"] = OidcTestClient.SignedOut,
            ["state"] = "logout-state"
        };
        var invalid = new Dictionary<string, string?>(parameters) { ["post_logout_redirect_uri"] = "https://evil.example/" };
        var response = await client.Browser.GetAsync(QueryHelpers.AddQueryString("/connect/logout", invalid));
        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Null(response.Headers.Location);
        response = await client.Browser.GetAsync(QueryHelpers.AddQueryString("/connect/logout", parameters));
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("Sign out?", html);
        Assert.Equal(HttpStatusCode.OK, (await client.Browser.GetAsync("/Account")).StatusCode);
        parameters["confirm"] = "yes";
        Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.PostAsync("/connect/logout", new FormUrlEncodedContent(parameters))).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.Browser.GetAsync("/Account")).StatusCode);
        parameters["__RequestVerificationToken"] = OidcTestClient.Field(html, "__RequestVerificationToken");
        response = await client.Browser.PostAsync("/connect/logout", new FormUrlEncodedContent(parameters));
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(OidcTestClient.SignedOut + "?state=logout-state", response.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.Browser.GetAsync("/Account")).StatusCode);
    }

    [PostgresFact]
    public async Task UserInfoRequiresABearerTokenAndRespectsGrantedScopesAndRevocation()
    {
        await using var client = await OidcTestClient.Create();
        var parameters = client.Parameters(); parameters["scope"] = "openid";
        var response = await client.Redeem(await client.Code(parameters));
        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = await client.ValidateIdToken(tokens.GetProperty("id_token").GetString()!);
        Assert.False(id.TryGetClaim("email", out _));
        Assert.False(id.TryGetClaim("name", out _));
        Assert.False(tokens.TryGetProperty("refresh_token", out _));
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.Browser.GetAsync("/connect/userinfo")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.UserInfo("tampered")).StatusCode);
        var access = tokens.GetProperty("access_token").GetString()!;
        var info = await (await client.UserInfo(access)).Content.ReadFromJsonAsync<JsonElement>();
        Assert.Single(info.EnumerateObject());
        Assert.Equal(id.Subject, info.GetProperty("sub").GetString());
        var entry = await client.Database.Db.Set<OpenIddictEntityFrameworkCoreToken>().SingleAsync(token => token.Type == TokenTypeIdentifiers.AccessToken);
        entry.Status = Statuses.Revoked;
        await client.Database.Db.SaveChangesAsync();
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.UserInfo(access)).StatusCode);
    }

    [PostgresFact]
    public async Task PermissionAndAuthorizationChangesInvalidateRefreshAndContinuationRequests()
    {
        await using var client = await OidcTestClient.Create();
        var tokens = await client.Tokens();
        var applications = client.Database.Services.GetRequiredService<IOpenIddictApplicationManager>();
        var application = (await applications.FindByClientIdAsync(OidcTestClient.ClientId))!;
        var descriptor = new OpenIddictApplicationDescriptor();
        await applications.PopulateAsync(descriptor, application);
        descriptor.Permissions.Remove(Permissions.Prefixes.Scope + "department");
        await applications.UpdateAsync(application, descriptor);
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Refresh(tokens.GetProperty("refresh_token").GetString()!)));
        descriptor.Permissions.Add(Permissions.Prefixes.Scope + "department");
        await applications.UpdateAsync(application, descriptor);
        tokens = await client.Tokens();
        client.Database.Db.ChangeTracker.Clear();
        foreach (var authorization in await client.Database.Db.Set<OpenIddictEntityFrameworkCoreAuthorization>().ToListAsync())
            authorization.Status = Statuses.Revoked;
        await client.Database.Db.SaveChangesAsync();
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Refresh(tokens.GetProperty("refresh_token").GetString()!)));

        var request = client.Parameters(); request["prompt"] = "login";
        var html = await (await client.Authorize(request)).Content.ReadAsStringAsync();
        await using var updateScope = client.Factory.Services.CreateAsyncScope();
        var updatedApplications = updateScope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        application = (await updatedApplications.FindByClientIdAsync(OidcTestClient.ClientId))!;
        await updatedApplications.PopulateAsync(descriptor, application);
        descriptor.RedirectUris.Clear(); descriptor.RedirectUris.Add(new Uri("https://client.example.test/replacement"));
        await updatedApplications.UpdateAsync(application, descriptor);
        var login = await client.SubmitLogin(html);
        var rejected = await client.Browser.GetAsync(login.Headers.Location);
        Assert.Equal(HttpStatusCode.BadRequest, rejected.StatusCode);
        Assert.Null(rejected.Headers.Location);
    }

    [PostgresFact]
    public async Task FormPostAuthorizationAndRefreshDownscopingWorkWithoutAddingConsentFlows()
    {
        await using var client = await OidcTestClient.Create();
        var request = client.Parameters();
        var response = await client.Browser.PostAsync("/connect/authorize", new FormUrlEncodedContent(request));
        Assert.Contains("account-shell oidc", await response.Content.ReadAsStringAsync());
        var tokens = await client.Tokens();
        response = await client.Refresh(tokens.GetProperty("refresh_token").GetString()!, "openid offline_access");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var narrower = await response.Content.ReadFromJsonAsync<JsonElement>();
        var id = await client.ValidateIdToken(narrower.GetProperty("id_token").GetString()!);
        Assert.False(id.TryGetClaim("email", out _));
        Assert.False(id.TryGetClaim("department", out _));
        request["prompt"] = "consent";
        Assert.Equal(Errors.ConsentRequired, await OidcTestClient.Error(await client.Authorize(request)));
    }
}
