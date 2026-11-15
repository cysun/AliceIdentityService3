using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Data;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAisData(this IServiceCollection services)
    {
        services.AddDbContext<AisDbContext>((provider, options) =>
        {
            var connection = provider.GetRequiredService<IConfiguration>().GetConnectionString("DefaultConnection")
                ?? throw new InvalidOperationException("Set ConnectionStrings:DefaultConnection in appsettings.json or environment variables.");
            options.UseNpgsql(connection);
            options.UseOpenIddict();
        });
        services.AddIdentityCore<User>(options =>
        {
            options.Stores.SchemaVersion = IdentitySchemaVersions.Version1;
            options.Stores.MaxLengthForKeys = 0;
            options.SignIn.RequireConfirmedEmail = true;
            options.Lockout.AllowedForNewUsers = true;
            options.Lockout.MaxFailedAccessAttempts = 5;
            options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            options.ClaimsIdentity.UserNameClaimType = Claims.Name;
            options.ClaimsIdentity.UserIdClaimType = Claims.Subject;
            options.ClaimsIdentity.RoleClaimType = Claims.Role;
            options.ClaimsIdentity.EmailClaimType = Claims.Email;
        }).AddEntityFrameworkStores<AisDbContext>().AddSignInManager();
        // The OIDC server starts in Step 4; only its persistence is registered here.
        services.AddOpenIddict().AddCore(options => options.UseEntityFrameworkCore().UseDbContext<AisDbContext>());
        services.AddScoped<BootstrapService>();
        return services;
    }
}
