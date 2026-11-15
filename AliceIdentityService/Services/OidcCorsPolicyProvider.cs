using Microsoft.AspNetCore.Cors.Infrastructure;
using OpenIddict.Abstractions;

namespace AliceIdentityService.Services;

// Browser clients may call bearer/token endpoints from their registered redirect
// origins. This never grants cross-origin access to account cookies or admin APIs.
public sealed class OidcCorsPolicyProvider(IOpenIddictApplicationManager applications) : ICorsPolicyProvider
{
    public async Task<CorsPolicy?> GetPolicyAsync(HttpContext context, string? policyName)
    {
        if (context.Request.Path.StartsWithSegments("/.well-known"))
            return new CorsPolicyBuilder().AllowAnyOrigin().WithMethods("GET").Build();
        if (context.Request.Path != "/connect/token" && context.Request.Path != "/connect/userinfo") return null;
        var origin = context.Request.Headers.Origin.ToString();
        if (!Uri.TryCreate(origin, UriKind.Absolute, out var requested) ||
            requested.Scheme is not ("https" or "http") || origin != requested.GetLeftPart(UriPartial.Authority)) return null;
        await foreach (var application in applications.ListAsync(cancellationToken: context.RequestAborted))
            foreach (var redirect in await applications.GetRedirectUrisAsync(application, context.RequestAborted))
                if (Uri.TryCreate(redirect, UriKind.Absolute, out var uri) &&
                    string.Equals(uri.GetLeftPart(UriPartial.Authority), origin, StringComparison.OrdinalIgnoreCase))
                    return new CorsPolicyBuilder().WithOrigins(origin).WithMethods("GET", "POST")
                        .WithHeaders("Content-Type", "Authorization").Build();
        return null;
    }
}
