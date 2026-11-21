using System.Text;
using AliceIdentityService.Models;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.WebUtilities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace AliceIdentityService.Services;

public sealed class PasswordResetTokenOptions : DataProtectionTokenProviderOptions
{
    public PasswordResetTokenOptions()
    {
        Name = "AIS3.PasswordReset.v1";
        TokenLifespan = TimeSpan.FromHours(1);
    }
}

public sealed class PasswordResetTokenProvider(IDataProtectionProvider protection,
    IOptions<PasswordResetTokenOptions> options, ILogger<DataProtectorTokenProvider<User>> logger)
    : DataProtectorTokenProvider<User>(protection, options, logger);

public sealed class PasswordRecoveryService(UserManager<User> users, IAccountEmailSender mail, AccountEmailLimit limit,
    ILogger<PasswordRecoveryService> logger)
{
    public async Task RequestAsync(string email, string? context, CancellationToken cancellationToken)
    {
        var normalized = users.NormalizeEmail(email.Trim());
        if (!limit.TryAcquire(normalized!)) return;
        var matches = await users.Users.Where(user => user.NormalizedEmail == normalized).Take(2).ToListAsync(cancellationToken);
        // Do not guess between legacy accounts sharing an email, or let recovery confirm an address.
        if (matches.Count != 1 || !matches[0].EmailConfirmed || !await users.HasPasswordAsync(matches[0])) return;
        var user = matches[0];
        var token = await users.GeneratePasswordResetTokenAsync(user);
        var path = QueryHelpers.AddQueryString("/Account/ResetPassword", new Dictionary<string, string?>
        {
            ["userId"] = user.Id,
            ["code"] = WebEncoders.Base64UrlEncode(Encoding.UTF8.GetBytes(token)),
            ["oidcContext"] = context
        });
        await mail.SendPasswordResetAsync(user, path, cancellationToken);
    }

    public async Task<User?> FindResetUserAsync(AccountEmailLinkModel input)
    {
        var token = Decode(input.Code);
        if (token is null) return null;
        var user = await users.FindByIdAsync(input.UserId);
        return user is not null && user.EmailConfirmed && await users.HasPasswordAsync(user) &&
            await users.VerifyUserTokenAsync(user, users.Options.Tokens.PasswordResetTokenProvider, "ResetPassword", token) ? user : null;
    }

    public async Task<IdentityResult> ResetAsync(SetPasswordInputModel input)
    {
        var user = await FindResetUserAsync(input);
        if (user is null) return IdentityResult.Failed(users.ErrorDescriber.InvalidToken());
        // Identity saves the password hash, security stamp and concurrency stamp in one update.
        // Concurrent uses cannot both succeed; all older reset links/cookies/grants become invalid.
        var result = await users.ResetPasswordAsync(user, Decode(input.Code)!, input.Password);
        if (result.Succeeded)
            logger.LogInformation("Password reset by email recovery for {Email} ({UserId})", OperationLog.Text(user.Email), user.Id);
        return result;
    }

    private static string? Decode(string code)
    {
        try { return Encoding.UTF8.GetString(WebEncoders.Base64UrlDecode(code)); }
        catch (FormatException) { return null; }
    }
}
