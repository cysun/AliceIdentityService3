using System.Data;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace AliceIdentityService.Services;

public sealed class AdminOverviewService(AisDbContext database, TimeProvider clock)
{
    public async Task<AdminUserCounts> ReadUserCountsAsync(DateTimeOffset now, CancellationToken cancellationToken) =>
        await database.Users.GroupBy(user => 1).Select(group => new AdminUserCounts(
            group.LongCount(),
            // Any ais_admin claim grants access. Duplicate claims still identify one account.
            group.LongCount(user => database.UserClaims.Any(claim => claim.UserId == user.Id && claim.ClaimType == AisConstants.AdminClaim)),
            group.LongCount(user => !user.EmailConfirmed),
            group.LongCount(user => user.LockoutEnabled && user.LockoutEnd > now)
        )).SingleOrDefaultAsync(cancellationToken) ?? new(0, 0, 0, 0);

    public async Task<AdminOverview> ReadAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        // Counts and bounded lists describe the same database snapshot, even during registrations.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var users = database.Users.AsNoTracking();
        var clients = database.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking();
        var userCounts = await ReadUserCountsAsync(now, cancellationToken);
        var applications = await clients.LongCountAsync(cancellationToken);
        var customScopes = await database.Set<OpenIddictEntityFrameworkCoreScope>()
            .LongCountAsync(scope => scope.Name == null || !AisConstants.StandardScopeNames.Contains(scope.Name), cancellationToken);
        var recent = await users.OrderByDescending(user => user.CreationTime).ThenBy(user => user.Id).Take(5)
            .Select(user => new RecentRegistration(user.Id,
                user.ScreenName != "" ? user.ScreenName : user.FirstName + " " + user.LastName, user.Email,
                user.LockoutEnabled && user.LockoutEnd > now ? "Locked" : user.EmailConfirmed ? "Confirmed" : "Unconfirmed",
                user.CreationTime)).ToListAsync(cancellationToken);
        var connected = await clients.OrderBy(client => client.DisplayName != null && client.DisplayName != ""
                ? client.DisplayName : client.ClientId ?? client.Id).ThenBy(client => client.Id)
            .Take(6).Select(client => new AdminApplication(client.Id!,
                client.DisplayName != null && client.DisplayName != "" ? client.DisplayName : client.ClientId ?? "Unnamed application",
                client.ClientId)).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(new(userCounts.TotalUsers, applications, userCounts.UnconfirmedUsers, userCounts.Administrators,
            userCounts.LockedUsers, customScopes), recent, connected, now);
    }
}
