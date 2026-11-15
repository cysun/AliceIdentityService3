using AliceIdentityService;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Serilog;

var bootstrap = args.FirstOrDefault() == "bootstrap";
var builder = WebApplication.CreateBuilder(bootstrap ? [] : args);
builder.Services.AddSerilog((services, logger) =>
    logger.ReadFrom.Configuration(services.GetRequiredService<IConfiguration>()));
builder.Services.AddAisData();
builder.Services.AddControllersWithViews();
builder.Services.AddHealthChecks().AddCheck<DatabaseReadinessCheck>("database", tags: ["ready"]);

var app = builder.Build();
if (bootstrap)
{
    Environment.ExitCode = await BootstrapCommand.RunAsync(app.Services, args[1..]);
    await app.DisposeAsync();
    return;
}

// After Build so EF tooling can obtain the registered context without starting the preview.
if (!app.Environment.IsDevelopment() && !app.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("AIS3 is a development-only UI preview. Authentication is not implemented.");

app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/Account/Login"));
app.MapGet("/api/system/info", () => ApplicationInfo.Current);
app.MapHealthChecks("/health", new HealthCheckOptions { Predicate = _ => false });
app.MapHealthChecks("/health/ready", new HealthCheckOptions { Predicate = check => check.Tags.Contains("ready") });
app.MapControllers();
app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");
app.Run();

public partial class Program;
