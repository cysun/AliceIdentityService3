using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Json;
using AliceIdentityService.Models;
using AliceIdentityService.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;

namespace AliceIdentityService.Services;

public sealed record AdministratorRequest(string Email, string FirstName, string LastName, string? ScreenName = null);
public sealed record BootstrapResult(bool AdministratorCreated, int ScopesCreated);

public sealed class BootstrapService(AisDbContext db, UserManager<User> users, IOpenIddictScopeManager scopes)
{
    public async Task<BootstrapResult> RunAsync(AdministratorRequest request, Func<Task<string>> readPassword, CancellationToken cancellationToken = default)
    {
        var email = request.Email.Trim();
        var firstName = RequiredName(request.FirstName, "First name");
        var lastName = RequiredName(request.LastName, "Last name");
        var screenName = RequiredName(request.ScreenName ?? $"{firstName} {lastName}", "Screen name");
        if (email.Length > 256 || !new EmailAddressAttribute().IsValid(email))
            throw new ArgumentException("A valid email address of at most 256 characters is required.");

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);
        await db.Database.ExecuteSqlRawAsync("SELECT pg_advisory_xact_lock(7193402002)", cancellationToken);
        var normalizedEmail = users.NormalizeEmail(email);
        var normalizedName = users.NormalizeName(email);
        var matches = await db.Users.Where(user => user.NormalizedEmail == normalizedEmail || user.NormalizedUserName == normalizedName)
            .ToListAsync(cancellationToken);
        var created = false;
        if (matches.Count > 1)
            throw new InvalidOperationException("Multiple existing accounts match the supplied email. Bootstrap cannot resolve them automatically.");
        if (matches.Count == 1)
        {
            var claims = await users.GetClaimsAsync(matches[0]);
            if (!claims.Any(claim => claim.Type == AisConstants.AdminClaim))
                throw new InvalidOperationException("This account already exists without administrator access. Bootstrap will not promote it or change its password.");
        }
        else
        {
            var user = new User
            {
                UserName = email,
                Email = email,
                FirstName = firstName,
                LastName = lastName,
                ScreenName = screenName,
                EmailConfirmed = true
            };
            RequireSuccess(await users.CreateAsync(user, await readPassword()));
            RequireSuccess(await users.AddClaimAsync(user, new Claim(AisConstants.AdminClaim, "true")));
            created = true;
        }
        var scopesCreated = 0;
        foreach (var (name, claims) in AisConstants.StandardScopes)
        {
            if (await scopes.FindByNameAsync(name, cancellationToken) is not null) continue;
            var descriptor = new OpenIddictScopeDescriptor { Name = name, DisplayName = name };
            descriptor.Properties["claims"] = JsonSerializer.SerializeToElement(claims);
            await scopes.CreateAsync(descriptor, cancellationToken);
            scopesCreated++;
        }
        await transaction.CommitAsync(cancellationToken);
        return new(created, scopesCreated);
    }

    private static string RequiredName(string value, string label)
    {
        value = value.Trim();
        if (value.Length is < 1 or > 255) throw new ArgumentException($"{label} must contain 1–255 characters.");
        return value;
    }

    private static void RequireSuccess(IdentityResult result)
    {
        if (!result.Succeeded) throw new InvalidOperationException("Administrator creation failed: " + string.Join(", ", result.Errors.Select(error => error.Code)));
    }
}
