using System.Globalization;
using System.Net;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

// Standalone local test client, not an AIS3 service or deployment component.
var builder = WebApplication.CreateBuilder(args);
builder.WebHost.UseUrls("https://localhost:9443");
builder.Services.AddAntiforgery(options => options.Cookie.SecurePolicy = CookieSecurePolicy.Always);
builder.Services.AddAuthentication(options =>
{
    options.DefaultScheme = CookieAuthenticationDefaults.AuthenticationScheme;
    options.DefaultChallengeScheme = OpenIdConnectDefaults.AuthenticationScheme;
}).AddCookie(options =>
{
    options.Cookie.Name = "__Host-AIS3.ReviewClient";
    options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
}).AddOpenIdConnect(options =>
{
    options.Authority = builder.Configuration["Authority"] ?? "https://localhost:7443/";
    options.ClientId = "ais3-review";
    options.ResponseType = "code";
    options.UsePkce = true;
    options.SaveTokens = true;
    options.MapInboundClaims = false;
    options.GetClaimsFromUserInfoEndpoint = true;
    options.Scope.Clear();
    foreach (var scope in new[] { "openid", "profile", "email", "offline_access" }) options.Scope.Add(scope);
    options.TokenValidationParameters.NameClaimType = "name";
});
builder.Services.AddAuthorization();
var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.MapGet("/", async (HttpContext context, IAntiforgery antiforgery) =>
{
    context.Response.Headers.CacheControl = "no-store";
    var token = WebUtility.HtmlEncode(antiforgery.GetAndStoreTokens(context).RequestToken);
    var body = "<p>This local client exercises AIS3 authorization code + PKCE, token renewal, userinfo, and logout.</p>";
    if (context.User.Identity?.IsAuthenticated == true)
    {
        var ticket = await context.AuthenticateAsync();
        body += "<p>Signed in as <strong>" + WebUtility.HtmlEncode(context.User.Identity.Name) + "</strong></p>";
        body += "<p>Access token expires: " + WebUtility.HtmlEncode(ticket.Properties!.GetTokenValue("expires_at")) + "</p>";
        body += "<table><tr><th>Claim</th><th>Value</th></tr>" + string.Join("", context.User.Claims.Select(claim =>
            "<tr><td>" + WebUtility.HtmlEncode(claim.Type) + "</td><td>" + WebUtility.HtmlEncode(claim.Value) + "</td></tr>")) + "</table>";
        foreach (var (action, text) in new[] { ("refresh", "Renew tokens"), ("logout", "Sign out through AIS3") })
            body += $"<form method=\"post\" action=\"/{action}\"><input type=\"hidden\" name=\"__RequestVerificationToken\" value=\"{token}\"/><button>{text}</button></form>";
        if (context.Request.Query.ContainsKey("renewed")) body += "<p role=\"status\">Tokens renewed successfully; the refreshed ID token signature, issuer, audience, and subject were validated.</p>";
    }
    else body += "<p><a href=\"/login\">Sign in with AIS3</a></p>";
    return Results.Content("<!doctype html><html lang=\"en\"><head><meta charset=\"utf-8\"><meta name=\"viewport\" content=\"width=device-width\"><title>AIS3 OIDC Review Client</title>" +
        "<style>body{font:16px/1.5 system-ui;max-width:900px;margin:48px auto;padding:0 20px;color:#182434}table{border-collapse:collapse;width:100%}td,th{text-align:left;padding:8px;border-bottom:1px solid #ccd3dd;overflow-wrap:anywhere}form{display:inline-block;margin:20px 12px 0 0}button{padding:10px 16px;cursor:pointer}a{color:#204fb5}</style></head><body><h1>AIS3 OIDC review</h1>" + body + "</body></html>", "text/html");
});
app.MapGet("/login", () => Results.Challenge(new AuthenticationProperties { RedirectUri = "/" }, [OpenIdConnectDefaults.AuthenticationScheme]));
app.MapPost("/refresh", async (HttpContext context, IAntiforgery antiforgery, IOptionsMonitor<OpenIdConnectOptions> options) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    var ticket = await context.AuthenticateAsync();
    var refresh = ticket.Properties?.GetTokenValue("refresh_token");
    if (refresh is null) return Results.BadRequest("No refresh token is available. Sign in again.");
    var settings = options.Get(OpenIdConnectDefaults.AuthenticationScheme);
    var configuration = await settings.ConfigurationManager!.GetConfigurationAsync(context.RequestAborted);
    using var response = await settings.Backchannel.PostAsync(configuration.TokenEndpoint, new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "refresh_token",
        ["refresh_token"] = refresh,
        ["client_id"] = settings.ClientId!
    }), context.RequestAborted);
    if (!response.IsSuccessStatusCode) return Results.BadRequest("Token renewal was rejected by AIS3. Sign out and sign in again.");
    var tokens = await response.Content.ReadFromJsonAsync<JsonElement>(context.RequestAborted);
    var validation = settings.TokenValidationParameters.Clone();
    validation.ValidIssuer = configuration.Issuer;
    validation.ValidAudience = settings.ClientId;
    validation.IssuerSigningKeys = configuration.SigningKeys;
    var result = await new JsonWebTokenHandler().ValidateTokenAsync(tokens.GetProperty("id_token").GetString(), validation);
    if (!result.IsValid || result.ClaimsIdentity.FindFirst("sub")?.Value != context.User.FindFirst("sub")?.Value)
        return Results.BadRequest("The refreshed ID token failed validation.");
    var saved = ticket.Properties!.GetTokens().ToDictionary(item => item.Name, item => item.Value);
    foreach (var name in new[] { "access_token", "id_token", "refresh_token", "token_type" })
        if (tokens.TryGetProperty(name, out var value)) saved[name] = value.GetString()!;
    saved["expires_at"] = DateTimeOffset.UtcNow.AddSeconds(tokens.GetProperty("expires_in").GetInt32()).ToString("o", CultureInfo.InvariantCulture);
    ticket.Properties!.StoreTokens(saved.Select(pair => new AuthenticationToken { Name = pair.Key, Value = pair.Value }));
    await context.SignInAsync(CookieAuthenticationDefaults.AuthenticationScheme, new ClaimsPrincipal(result.ClaimsIdentity), ticket.Properties);
    return Results.Redirect("/?renewed=1");
}).RequireAuthorization();
app.MapPost("/logout", async (HttpContext context, IAntiforgery antiforgery) =>
{
    try { await antiforgery.ValidateRequestAsync(context); }
    catch (AntiforgeryValidationException) { return Results.BadRequest(); }
    return Results.SignOut(new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]);
}).RequireAuthorization();
app.Run();
