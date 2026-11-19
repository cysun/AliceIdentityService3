using System.Text;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AliceIdentityService.Services;

public sealed class RegistrationService(UserManager<User> users, AisDbContext database,
    IAccountEmailSender mail, AccountEmailLimit limit)
{
    public async Task<IdentityResult> RegisterAsync(RegistrationInputModel input, CancellationToken cancellationToken)
    {
        var user = new User
        {
            UserName = input.Email.Trim(),
            Email = input.Email.Trim(),
            FirstName = input.FirstName.Trim(),
            LastName = input.LastName.Trim()
        };
        var fullName = user.FullName;
        user.ScreenName = fullName.Length <= 255 ? fullName : fullName[..(char.IsHighSurrogate(fullName[254]) ? 254 : 255)];
        var normalized = users.NormalizeEmail(user.Email);
        if (!await users.Users.AnyAsync(existing => existing.NormalizedEmail == normalized ||
            existing.NormalizedUserName == users.NormalizeName(user.UserName), cancellationToken))
        {
            IdentityResult result;
            try { result = await users.CreateAsync(user); }
            catch (DbUpdateException error) when (error.InnerException is PostgresException
            { SqlState: PostgresErrorCodes.UniqueViolation, ConstraintName: "UserNameIndex" })
            {
                // A concurrent registration won the unique username race. Never overwrite it.
                database.ChangeTracker.Clear();
                result = IdentityResult.Success;
            }
            if (!result.Succeeded && result.Errors.Any(error => error.Code is not ("DuplicateEmail" or "DuplicateUserName")))
                return result;
        }
        // Existing accounts keep their password/profile. Only a matching unconfirmed
        // address is eligible for another message, subject to the same resend limit.
        await ResendAsync(user.Email!, input.OidcContext, cancellationToken);
        return IdentityResult.Success;
    }

    public async Task ResendAsync(string email, string? context, CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email.Trim());
        if (!limit.TryAcquire(normalized!)) return;
        var matches = await users.Users.Where(user => user.NormalizedEmail == normalized).Take(2).ToListAsync(cancellationToken);
        // A legacy database can contain ambiguous email records. Do not guess an identity.
        if (matches.Count != 1 || matches[0].EmailConfirmed) return;
        var user = matches[0];
        var token = await users.GenerateEmailConfirmationTokenAsync(user);
        var path = QueryHelpers.AddQueryString("/Account/ConfirmEmail", new Dictionary<string, string?>
        {
            ["userId"] = user.Id,
            ["code"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)),
            ["oidcContext"] = context
        });
        await mail.SendConfirmationAsync(user.Email!, user.FullName, path, cancellationToken);
    }

    public async Task<bool> CanConfirmAsync(AccountEmailLinkModel input)
    {
        var user = await users.FindByIdAsync(input.UserId);
        var token = Decode(input.Code);
        return user is not null && !user.EmailConfirmed && token is not null &&
            await users.VerifyUserTokenAsync(user, users.Options.Tokens.EmailConfirmationTokenProvider, "EmailConfirmation", token);
    }

    public async Task<IdentityResult> ConfirmAsync(SetPasswordInputModel input, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        var user = await users.FindByIdAsync(input.UserId);
        var token = Decode(input.Code);
        if (user is null || user.EmailConfirmed || token is null)
            return IdentityResult.Failed(users.ErrorDescriber.InvalidToken());
        var confirmed = await users.ConfirmEmailAsync(user, token);
        if (!confirmed.Succeeded) return confirmed;

        // Identity applies the password policy and rotates the stamp for both new
        // passwordless registrations and pending accounts with an older password.
        var resetToken = await users.GeneratePasswordResetTokenAsync(user);
        var passwordResult = await users.ResetPasswordAsync(user, resetToken, input.Password);
        if (!passwordResult.Succeeded)
        {
            // The transaction rolls back confirmation. Discard the provisional tracked state too.
            database.Entry(user).State = EntityState.Detached;
            return passwordResult;
        }
        await transaction.CommitAsync(cancellationToken);
        return IdentityResult.Success;
    }

    private static string? Decode(string code)
    {
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return null; }
    }
}
