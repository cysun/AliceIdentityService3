using System.Security.Claims;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using OpenIddict.Abstractions;

namespace AliceIdentityService.Services;

public sealed record AdminMutationOutcome(int Status, string? Id = null, bool SessionEnded = false, string? Error = null);

public sealed class AdminUserMutationService(AisDbContext database, UserManager<User> users, TimeProvider clock, AdminWriteGuard guard,
    IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, ILogger<AdminUserMutationService> logger)
{
    private sealed record Change(IdentityResult Result, bool Revoke = false);
    private static Change Invalid(string message) => new(IdentityResult.Failed(new IdentityError { Description = message }));

    public async Task<AdminMutationOutcome> CreateAsync(ClaimsPrincipal actor, AdminCreateUserInput input, CancellationToken cancellationToken)
    {
        await using var transaction = await guard.BeginAsync(cancellationToken);
        if (await guard.CheckActorAsync(actor, cancellationToken) is { } denied) return denied;
        var email = input.Email.Trim();
        var normalizedEmail = users.NormalizeEmail(email);
        var normalizedName = users.NormalizeName(email);
        if (await database.Users.AnyAsync(user => user.NormalizedEmail == normalizedEmail || user.NormalizedUserName == normalizedName, cancellationToken))
            return new(400, Error: "An account already uses this email address.");
        var user = new User
        {
            UserName = email,
            Email = email,
            EmailConfirmed = true,
            FirstName = input.FirstName.Trim(),
            LastName = input.LastName.Trim(),
            ScreenName = input.ScreenName.Trim()
        };
        IdentityResult result;
        try { result = await users.CreateAsync(user, input.Password); }
        catch (DbUpdateException error) when (error.InnerException is PostgresException
        { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UserNameIndex" })
        {
            return new(400, Error: "An account already uses this email address.");
        }
        if (!result.Succeeded) return Failure(result);
        await transaction.CommitAsync(cancellationToken);
        Audit("create", users.GetUserId(actor)!, user.Id);
        return new(200, user.Id);
    }

    public Task<AdminMutationOutcome> EditAsync(ClaimsPrincipal actor, string id, AdminEditUserInput input, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, input.Version, "edit", async (user, actorId) =>
        {
            if (actorId == id && !input.EmailConfirmed) return Invalid("You cannot unconfirm your own account.");
            if (!user.EmailConfirmed && input.EmailConfirmed && string.IsNullOrWhiteSpace(user.Email)) return Invalid("An account without an email address cannot be confirmed.");
            var changedConfirmation = user.EmailConfirmed != input.EmailConfirmed;
            user.FirstName = input.FirstName.Trim();
            user.LastName = input.LastName.Trim();
            user.ScreenName = input.ScreenName.Trim();
            user.EmailConfirmed = input.EmailConfirmed;
            var result = changedConfirmation ? await users.UpdateSecurityStampAsync(user) : await users.UpdateAsync(user);
            return new(result, changedConfirmation);
        }, cancellationToken);

    public Task<AdminMutationOutcome> PasswordAsync(ClaimsPrincipal actor, string id, AdminPasswordInput input, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, input.Version, "password-reset", async (user, _) =>
        {
            var token = await users.GeneratePasswordResetTokenAsync(user);
            // Identity atomically changes the hash and stamp, applying the existing password policy.
            return new(await users.ResetPasswordAsync(user, token, input.Password), true);
        }, cancellationToken);

    public Task<AdminMutationOutcome> LockAsync(ClaimsPrincipal actor, string id, AdminLockInput input, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, input.Version, "lock", async (user, actorId) =>
        {
            if (actorId == id) return Invalid("You cannot lock your own account.");
            user.LockoutEnabled = true;
            user.LockoutEnd = input.Duration switch
            {
                "hour" => clock.GetUtcNow().AddHours(1),
                "day" => clock.GetUtcNow().AddDays(1),
                _ => DateTimeOffset.MaxValue
            };
            return new(await users.UpdateSecurityStampAsync(user), true);
        }, cancellationToken);

    public Task<AdminMutationOutcome> UnlockAsync(ClaimsPrincipal actor, string id, AdminUserVersionInput input, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, input.Version, "unlock", async (user, _) =>
        {
            user.LockoutEnd = null;
            user.AccessFailedCount = 0;
            // Imported lockouts may predate stamp rotation; do not revive old sessions on unlock.
            return new(await users.UpdateSecurityStampAsync(user), true);
        }, cancellationToken);

    public Task<AdminMutationOutcome> DeleteAsync(ClaimsPrincipal actor, string id, AdminDeleteUserInput input, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, input.Version, "delete", async (user, actorId) =>
        {
            if (actorId == id) return Invalid("You cannot delete your own account.");
            var identifier = string.IsNullOrWhiteSpace(user.Email) ? user.Id : user.Email;
            if (!string.Equals(input.Confirmation.Trim(), identifier, StringComparison.Ordinal))
                return Invalid("Enter the account's email address (or user ID when it has no email) to confirm deletion.");
            return new(await users.DeleteAsync(user), true);
        }, cancellationToken);

    public Task<AdminMutationOutcome> AddClaimAsync(ClaimsPrincipal actor, string id, AdminClaimInput input, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, input.Version, "claim-add", async (user, _) =>
        {
            var type = input.Type.Trim();
            var value = input.Value.Trim();
            if (!AdminClaimRules.ValidType(type) || AdminClaimRules.ManagedByIdentity(type))
                return Invalid("Use a custom claim type; identity and protocol claims are managed by the service.");
            if (value.Any(char.IsControl)) return Invalid("Claim values cannot contain control characters.");
            if (await database.UserClaims.AnyAsync(claim => claim.UserId == id && claim.ClaimType == type && claim.ClaimValue == value, cancellationToken))
                return Invalid("This account already has that claim and value.");
            database.UserClaims.Add(new IdentityUserClaim<string> { UserId = id, ClaimType = type, ClaimValue = value });
            return new(await users.UpdateSecurityStampAsync(user), true);
        }, cancellationToken);

    public Task<AdminMutationOutcome> RemoveClaimAsync(ClaimsPrincipal actor, string id, int claimId, AdminUserVersionInput input, CancellationToken cancellationToken) =>
        ChangeAsync(actor, id, input.Version, "claim-remove", async (user, actorId) =>
        {
            var claim = await database.UserClaims.SingleOrDefaultAsync(claim => claim.UserId == id && claim.Id == claimId, cancellationToken);
            if (claim is null) return Invalid("This claim no longer exists. Reload the account.");
            if (actorId == id && claim.ClaimType == AisConstants.AdminClaim &&
                !await database.UserClaims.AnyAsync(other => other.UserId == id && other.ClaimType == AisConstants.AdminClaim && other.Id != claimId, cancellationToken))
                return Invalid("You cannot remove your own last administrator claim.");
            // Address a stored row so imported duplicate or null claims can be removed individually.
            database.UserClaims.Remove(claim);
            return new(await users.UpdateSecurityStampAsync(user), true);
        }, cancellationToken);

    private async Task<AdminMutationOutcome> ChangeAsync(ClaimsPrincipal actor, string id, string version, string operation,
        Func<User, string, Task<Change>> change, CancellationToken cancellationToken)
    {
        await using var transaction = await guard.BeginAsync(cancellationToken);
        if (await guard.CheckActorAsync(actor, cancellationToken) is { } denied) return denied;
        var user = await users.FindByIdAsync(id);
        if (user is null) return new(404, Error: "This account no longer exists.");
        if (AdminUserVersion.For(user.Id, user.ConcurrencyStamp) != version) return Conflict();
        var actorId = users.GetUserId(actor)!;
        var result = await change(user, actorId);
        if (!result.Result.Succeeded) return Failure(result.Result);
        if (result.Revoke)
        {
            // Same scoped DbContext/transaction as Identity: failure rolls back the entire operation.
            await tokens.RevokeBySubjectAsync(id, cancellationToken);
            await authorizations.RevokeBySubjectAsync(id, cancellationToken);
        }
        await transaction.CommitAsync(cancellationToken);
        Audit(operation, actorId, id);
        return new(200, id, result.Revoke && actorId == id);
    }

    private static AdminMutationOutcome Conflict() => new(409, Error: "This account has changed. Reload its details before trying again.");
    private static AdminMutationOutcome Failure(IdentityResult result) => result.Errors.Any(error => error.Code == "ConcurrencyFailure")
        ? Conflict() : new(400, Error: string.Join(" ", result.Errors.Select(error => error.Description)));
    private void Audit(string operation, string actorId, string userId) =>
        logger.LogInformation("Administrator {ActorId} performed {Operation} on account {UserId}", actorId, operation, userId);
}
