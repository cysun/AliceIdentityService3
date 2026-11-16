using System.Security.Claims;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;

namespace AliceIdentityService.Services;

// Every administrative mutation shares this transaction boundary and fresh actor check.
public sealed class AdminWriteGuard(AisDbContext database, UserManager<User> users, TimeProvider clock)
{
    public async Task<IDbContextTransaction> BeginAsync(CancellationToken cancellationToken)
    {
        var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        try
        {
            // Serialize administrative writes across instances, including actor revalidation.
            // Otherwise two admitted requests could lock/delete each other's administrators.
            await database.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7193402009)", cancellationToken);
            database.ChangeTracker.Clear(); // Discard the Identity entities read by cookie authentication.
            return transaction;
        }
        catch { await transaction.DisposeAsync(); throw; }
    }

    public async Task<AdminMutationOutcome?> CheckActorAsync(ClaimsPrincipal actor, CancellationToken cancellationToken)
    {
        var id = users.GetUserId(actor);
        var user = await database.Users.AsNoTracking().SingleOrDefaultAsync(user => user.Id == id, cancellationToken);
        var stamp = actor.FindFirstValue(users.Options.ClaimsIdentity.SecurityStampClaimType);
        if (user is null || stamp is null || stamp != user.SecurityStamp || !user.EmailConfirmed || user.TwoFactorEnabled ||
            user.LockoutEnabled && user.LockoutEnd > clock.GetUtcNow()) return new(401);
        if (!await database.UserClaims.AnyAsync(claim => claim.UserId == id && claim.ClaimType == AisConstants.AdminClaim, cancellationToken))
            return new(403);
        return null;
    }
}
