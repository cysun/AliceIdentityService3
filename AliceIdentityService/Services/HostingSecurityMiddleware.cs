namespace AliceIdentityService.Services;

public sealed class HostingSecurityMiddleware(RequestDelegate next, IHostEnvironment environment, ILogger<HostingSecurityMiddleware> logger)
{
    public async Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers["X-Content-Type-Options"] = "nosniff";
            context.Response.Headers["X-Frame-Options"] = "DENY";
            context.Response.Headers["Referrer-Policy"] = "no-referrer";
            context.Response.Headers["Permissions-Policy"] = "camera=(), microphone=(), geolocation=()";
            if (environment.IsDeployed())
            {
                // OpenIddict's form_post response renders a protocol form/script targeting the registered client.
                // Keep that response mode compatible; the account/admin documents use the stricter policy.
                context.Response.Headers.ContentSecurityPolicy = context.Request.Path.StartsWithSegments("/connect")
                    ? "frame-ancestors 'none'; base-uri 'self'; object-src 'none'"
                    : "default-src 'self'; script-src 'self'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; font-src 'self'; connect-src 'self'; frame-ancestors 'none'; base-uri 'self'; object-src 'none'; form-action 'self'";
            }
            return Task.CompletedTask;
        });
        try { await next(context); }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            if (!context.Response.HasStarted) context.Response.StatusCode = 499;
        }
        catch (Exception error) when (environment.IsDeployed())
        {
            var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "middleware";
            logger.LogError("Unhandled {FailureType} at {Route} (trace {TraceId})", error.GetType().Name, route, context.TraceIdentifier);
            if (context.Response.HasStarted) context.Abort();
            else
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsync("The request could not be completed. Please try again.");
            }
        }
    }
}
