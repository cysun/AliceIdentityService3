using AliceIdentityService.Models;
using Microsoft.AspNetCore.Identity;

namespace AliceIdentityService.Services;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddAisAuthentication(this IServiceCollection services)
    {
        services.AddAuthentication(IdentityConstants.ApplicationScheme).AddIdentityCookies();
        services.AddScoped<AisCookieEvents>();
        services.ConfigureApplicationCookie(options =>
        {
            options.Cookie.Name = "__Host-AIS3.Session";
            options.Cookie.Path = "/";
            options.Cookie.HttpOnly = true;
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.LoginPath = "/Account/Login";
            options.LogoutPath = "/Account/Logout";
            options.AccessDeniedPath = "/Account/AccessDenied";
            options.ExpireTimeSpan = TimeSpan.FromHours(8);
            options.SlidingExpiration = false;
            options.EventsType = typeof(AisCookieEvents);
        });
        services.AddAntiforgery(options =>
        {
            options.HeaderName = "X-CSRF-TOKEN";
            options.Cookie.Name = "__Host-AIS3.Antiforgery";
            options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
            options.Cookie.SameSite = SameSiteMode.Strict;
        });
        services.AddAuthorization(options => options.AddPolicy(AisConstants.AdminPolicy,
            policy => policy.RequireAuthenticatedUser().RequireClaim(AisConstants.AdminClaim)));
        return services;
    }
}
