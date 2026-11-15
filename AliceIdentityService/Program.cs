using AliceIdentityService;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Serilog;

var bootstrap = args.FirstOrDefault() == "bootstrap";
var reviewClient = args.FirstOrDefault() == "review-client";
var builder = WebApplication.CreateBuilder(bootstrap || reviewClient ? [] : args);
builder.Services.AddSerilog((services, logger) =>
    logger.ReadFrom.Configuration(services.GetRequiredService<IConfiguration>()));
builder.Services.AddAisData();
builder.Services.AddAisAuthentication();
builder.Services.AddAisRegistration();
builder.Services.AddScoped<AdminOverviewService>();
builder.Services.AddScoped<AdminUserService>();
if (!bootstrap && !reviewClient) builder.Services.AddAisOpenIddict(builder.Configuration, builder.Environment);
// The Vite runner binds only HTTP on the backend, so endpoint discovery alone
// cannot determine the public HTTPS port. Keep redirects active in that mode too.
builder.Services.AddHttpsRedirection(options => options.HttpsPort = builder.Configuration.GetValue("HTTPS_PORT", 7443));
builder.Services.AddControllersWithViews(options => options.Filters.Add(new AutoValidateAntiforgeryTokenAttribute()));
// The development HTTPS proxy is local. Do not trust forwarded headers from arbitrary hosts.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.KnownProxies.Add(System.Net.IPAddress.Loopback);
});
builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"]);

var app = builder.Build();
if (reviewClient)
{
    Environment.ExitCode = await ReviewClientCommand.RunAsync(app.Services, app.Environment);
    await app.DisposeAsync();
    return;
}
if (bootstrap)
{
    Environment.ExitCode = await BootstrapCommand.RunAsync(app.Services, args[1..]);
    await app.DisposeAsync();
    return;
}

// After Build so EF tooling can obtain the registered context without starting the preview.
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("AIS3 is development-only until the staging and hardening review is complete.");

app.UseForwardedHeaders();
// /health remains usable by the local development runner before the HTTPS proxy starts.
app.UseWhen(context => !context.Request.Path.StartsWithSegments("/health"), branch => branch.UseHttpsRedirection());
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/connect") || context.Request.Path.StartsWithSegments("/Account"))
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers["Referrer-Policy"] = "no-referrer";
    }
    await next(context);
});
app.UseCors();
app.UseAuthentication();
// Static files bypass endpoint authorization. Guard the entire admin namespace
// before serving its index, bundles, or fallback routes.
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/Account") || context.Request.Path.StartsWithSegments("/admin") ||
        context.Request.Path.StartsWithSegments("/api"))
        context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Path.StartsWithSegments("/admin"))
    {
        var authorization = context.RequestServices.GetRequiredService<IAuthorizationService>();
        if (!(await authorization.AuthorizeAsync(context.User, null, AisConstants.AdminPolicy)).Succeeded)
        {
            if (context.User.Identity?.IsAuthenticated == true) await context.ForbidAsync();
            else await context.ChallengeAsync();
            return;
        }
    }
    await next(context);
});
app.UseAuthorization();
app.UseRateLimiter();
app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/Account"));
app.MapGet("/api/system/info", () => ApplicationInfo.Current).RequireAuthorization(AisConstants.AdminPolicy);
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapControllers();
app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");
app.Run();

public partial class Program;
