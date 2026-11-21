using System.Security.Claims;
using System.Text.RegularExpressions;
using AliceIdentityService.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Services;

internal static partial class OperationLog
{
    // Keep user-entered text on one line in the console, journal and rolling files.
    [GeneratedRegex(@"[\p{Cc}\p{Zl}\p{Zp}]")]
    private static partial Regex ControlCharacters();

    public static string? Text(string? value) => value is null ? null : ControlCharacters().Replace(value, " ");

    public static Dictionary<string, object?> Profile(User user) => new()
    {
        [nameof(user.FirstName)] = Text(user.FirstName),
        [nameof(user.LastName)] = Text(user.LastName),
        [nameof(user.ScreenName)] = Text(user.ScreenName),
        [nameof(user.EmailConfirmed)] = user.EmailConfirmed
    };

    public static string RedirectEndpoint(Uri uri) => uri.Scheme is "https" or "http"
        ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
        : uri.Scheme + ":";

    public static string ScopeResource(string value)
    {
        var resource = value.Trim();
        return Uri.TryCreate(resource, UriKind.Absolute, out var uri)
            ? uri.GetComponents(UriComponents.SchemeAndServer | UriComponents.Path, UriFormat.UriEscaped)
            : resource;
    }

    public static void Admin(ILogger logger, ClaimsPrincipal actor, string operation, string targetType,
        string? targetName, string? targetId, object? parameters = null)
    {
        var email = Text(actor.FindFirstValue(Claims.Email));
        var actorId = Text(actor.FindFirstValue(Claims.Subject));
        const string message = "Administrator {ActorEmail} ({ActorId}) {Operation} {TargetType} {TargetName} ({TargetId})";
        if (parameters is null)
            logger.LogInformation(message, email, actorId, operation, targetType, Text(targetName), Text(targetId));
        else
            logger.LogInformation(message + " with {@Parameters}", email, actorId, operation, targetType, Text(targetName), Text(targetId), parameters);
    }
}
