using System.Data;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using Microsoft.EntityFrameworkCore;
using OpenIddict.EntityFrameworkCore.Models;

namespace AliceIdentityService.Services;

public sealed class AdminOverviewService(AisDbContext database, TimeProvider clock)
{
    public async Task<AdminOverview> ReadAsync(CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        // Counts and bounded lists describe the same database snapshot, even during registrations.
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var users = database.Users.AsNoTracking();
        var clients = database.Set<OpenIddictEntityFrameworkCoreApplication>().AsNoTracking();
        var total = await users.LongCountAsync(cancellationToken);
        var applications = await clients.LongCountAsync(cancellationToken);
        var unconfirmed = await users.LongCountAsync(user => !user.EmailConfirmed, cancellationToken);
        // Count accounts, not claims: AIS2 permits duplicate claims and any ais_admin value grants access.
        var administrators = await users.LongCountAsync(user => database.UserClaims.Any(claim =>
            claim.UserId == user.Id && claim.ClaimType == AisConstants.AdminClaim), cancellationToken);
        var locked = await users.LongCountAsync(user => user.LockoutEnabled && user.LockoutEnd > now, cancellationToken);
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
        return new(new(total, applications, unconfirmed, administrators, locked), recent, connected, now);
    }
}
