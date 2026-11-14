using AliceIdentityService.Core;

var builder = WebApplication.CreateBuilder(args);

// The Step 1 shells are intentionally unauthenticated. Never enable them in production.
if (!builder.Environment.IsDevelopment() && !builder.Environment.IsEnvironment("Testing"))
    throw new InvalidOperationException("AIS3 Step 1 is a development-only UI preview. Authentication is not implemented.");

builder.Services.AddControllersWithViews();
builder.Services.AddHealthChecks();

var app = builder.Build();
app.UseStaticFiles();
app.MapGet("/", () => Results.Redirect("/Account/Login"));
app.MapGet("/api/system/info", () => ApplicationInfo.Current);
app.MapHealthChecks("/health");
app.MapControllers();
// A scoped fallback prevents unknown API, account, and OIDC paths from returning SPA HTML.
app.MapFallbackToFile("/admin/{*path:nonfile}", "admin/index.html");

app.Run();

public partial class Program;
