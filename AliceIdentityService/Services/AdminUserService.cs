using System.Data;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using Microsoft.EntityFrameworkCore;

namespace AliceIdentityService.Services;

public sealed class AdminUserService(AisDbContext database, TimeProvider clock)
{
    public static string[] SearchTerms(string? query) =>
        (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.Ordinal).ToArray();

    private static string Pattern(string term) => "%" + term.Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";

    public async Task<AdminUserPage> SearchAsync(AdminUserQuery input, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var query = database.Users.AsNoTracking();
        if (input.AdministratorsOnly)
            query = query.Where(user => database.UserClaims.Any(claim => claim.UserId == user.Id && claim.ClaimType == AisConstants.AdminClaim));
        foreach (var term in SearchTerms(input.Q))
        {
            var pattern = Pattern(term);
            // Keep this expression identical to IX_AspNetUsers_Search in PopulateSchema.sql.
            query = query.Where(user => EF.Functions.ILike(
                user.FirstName + " " + user.LastName + " " + user.ScreenName + " " + (user.Email ?? ""), pattern, "\\"));
        }
        query = input.Status switch
        {
            "Confirmed" => query.Where(user => user.EmailConfirmed),
            "Unconfirmed" => query.Where(user => !user.EmailConfirmed),
            "Locked" => query.Where(user => user.LockoutEnabled && user.LockoutEnd > now),
            _ => query
        };
        var count = await query.LongCountAsync(cancellationToken);
        // A bookmark can outlive its last page (for example, when another admin deletes an account).
        var page = (int)Math.Min(input.Page, Math.Max(1, (count + input.PageSize - 1) / input.PageSize));
        var descending = input.Direction == "desc";
        var ordered = input.Sort switch
        {
            "joined" => descending ? query.OrderByDescending(user => user.CreationTime) : query.OrderBy(user => user.CreationTime),
            "status" => descending
                ? query.OrderByDescending(user => user.LockoutEnabled && user.LockoutEnd > now ? "Locked" : user.EmailConfirmed ? "Confirmed" : "Unconfirmed")
                : query.OrderBy(user => user.LockoutEnabled && user.LockoutEnd > now ? "Locked" : user.EmailConfirmed ? "Confirmed" : "Unconfirmed"),
            _ => descending
                ? query.OrderByDescending(user => (user.ScreenName != "" ? user.ScreenName : user.FirstName + " " + user.LastName).ToLower())
                : query.OrderBy(user => (user.ScreenName != "" ? user.ScreenName : user.FirstName + " " + user.LastName).ToLower())
        };
        var items = await Summaries(ordered.ThenBy(user => user.Id).Skip((page - 1) * input.PageSize).Take(input.PageSize), now)
            .ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(items, count, page, input.PageSize, now);
    }

    public async Task<AdminUserDetail?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow();
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var query = database.Users.AsNoTracking().Where(user => user.Id == id);
        var summary = await Summaries(query, now).SingleOrDefaultAsync(cancellationToken);
        if (summary is null) return null;
        // Project explicit fields: Identity entities, hashes, stamps and token/login secrets never leave the store.
        var profile = await query.Select(user => new
        {
            user.FirstName,
            user.LastName,
            user.ScreenName,
            user.EmailConfirmed,
            user.ConcurrencyStamp,
            Security = new AdminUserSecurity(user.PasswordHash != null, user.TwoFactorEnabled,
                user.LockoutEnabled, user.LockoutEnd, user.AccessFailedCount)
        }).SingleAsync(cancellationToken);
        var claims = await database.UserClaims.AsNoTracking().Where(claim => claim.UserId == id)
            .OrderBy(claim => claim.ClaimType).ThenBy(claim => claim.Id)
            .Select(claim => new AdminUserClaim(claim.ClaimType, claim.ClaimValue, claim.Id)).ToListAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new(summary, profile.FirstName, profile.LastName, profile.ScreenName, profile.EmailConfirmed, profile.Security, claims, AdminUserVersion.For(id, profile.ConcurrencyStamp));
    }

    private IQueryable<AdminUserSummary> Summaries(IQueryable<User> users, DateTimeOffset now) =>
        users.Select(user => new AdminUserSummary(user.Id,
            user.ScreenName != "" ? user.ScreenName : user.FirstName + " " + user.LastName, user.Email,
            user.LockoutEnabled && user.LockoutEnd > now ? "Locked" : user.EmailConfirmed ? "Confirmed" : "Unconfirmed",
            user.CreationTime, database.UserClaims.Any(claim => claim.UserId == user.Id && claim.ClaimType == AisConstants.AdminClaim)));
}
