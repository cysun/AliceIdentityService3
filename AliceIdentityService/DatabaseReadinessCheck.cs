using AliceIdentityService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace AliceIdentityService;

public sealed class DatabaseReadinessCheck(IServiceScopeFactory scopes) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(HealthCheckContext context, CancellationToken cancellationToken = default)
    {
        try
        {
            await using var scope = scopes.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AisDbContext>();
            // An empty, initialized database is ready too. Local migration files/history are irrelevant.
            _ = await db.Users.AsNoTracking().AnyAsync(cancellationToken);
            return HealthCheckResult.Healthy();
        }
        catch (Exception error) when (error is Npgsql.NpgsqlException or InvalidOperationException)
        {
            return HealthCheckResult.Unhealthy("Database is unavailable.");
        }
    }
}
