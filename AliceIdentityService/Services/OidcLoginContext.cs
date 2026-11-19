using System.Security.Cryptography;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.WebUtilities;
using OpenIddict.Abstractions;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Services;

public sealed record ValidatedLoginContext(string ClientId, string AuthorizationUrl, string? RequiredSubject = null);
public sealed record LoginContextDetails(string ApplicationName, ValidatedLoginContext Request);

// Only the validated authorization endpoint can create this ten-minute continuation.
// Neither a client display name nor a return URL from browser input establishes OIDC context.
public sealed class OidcLoginContext(IDataProtectionProvider protection, IOpenIddictApplicationManager applications)
{
    private readonly ITimeLimitedDataProtector protector = protection.CreateProtector("AIS3.OidcLoginContext.v1").ToTimeLimitedDataProtector();

    public string Create(string clientId, string authorizationUrl, string? requiredSubject = null) =>
        protector.Protect(JsonSerializer.Serialize(new ValidatedLoginContext(clientId, authorizationUrl, requiredSubject)), TimeSpan.FromMinutes(10));

    public async Task<LoginContextDetails?> ReadAsync(string token)
    {
        if (token.Length > 20000) return null;
        try
        {
            var request = JsonSerializer.Deserialize<ValidatedLoginContext>(protector.Unprotect(token));
            if (request is null) return null;
            var application = await applications.FindByClientIdAsync(request.ClientId);
            if (application is null) return null;
            return new(await applications.GetDisplayNameAsync(application) ?? request.ClientId, request);
        }
        catch (Exception error) when (error is CryptographicException or JsonException or FormatException)
        {
            return null;
        }
    }

    public static string AfterPasswordSignIn(ValidatedLoginContext context)
    {
        var parts = context.AuthorizationUrl.Split('?', 2);
        var parameters = QueryHelpers.ParseQuery(parts.Length == 2 ? parts[1] : "");
        // These challenges have just been satisfied by a real password check. No
        // TempData flag or browser-controlled skip parameter can bypass reauthentication.
        if (parameters.TryGetValue(Parameters.Prompt, out var prompt))
        {
            var remaining = prompt.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .Where(value => value is not PromptValues.Login and not PromptValues.SelectAccount);
            parameters[Parameters.Prompt] = string.Join(' ', remaining);
            if (string.IsNullOrEmpty(parameters[Parameters.Prompt])) parameters.Remove(Parameters.Prompt);
        }
        if (parameters.TryGetValue(Parameters.MaxAge, out var maxAge) &&
            long.TryParse(maxAge, NumberStyles.Integer, CultureInfo.InvariantCulture, out var seconds) && seconds == 0)
            parameters.Remove(Parameters.MaxAge);
        // The authorization endpoint validates the full request again, including current client settings.
        return parts[0] + QueryString.Create(parameters);
    }
}
