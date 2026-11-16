using System.Data;
using System.Net;
using System.Security.Claims;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Services;

public sealed class AdminClientService(AisDbContext database, AdminWriteGuard guard, IOpenIddictApplicationManager applications,
    IOpenIddictScopeManager scopes, IOpenIddictTokenManager tokens, IOpenIddictAuthorizationManager authorizations, ILogger<AdminClientService> logger)
{
    private IQueryable<OpenIddictEntityFrameworkCoreApplication> Records => database.Set<OpenIddictEntityFrameworkCoreApplication>();

    public async Task<AdminClientPage> SearchAsync(AdminClientQuery input, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var query = Records.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(input.Q))
        {
            var pattern = "%" + input.Q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(client => EF.Functions.ILike((client.ClientId ?? "") + " " + (client.DisplayName ?? ""), pattern, "\\"));
        }
        var count = await query.LongCountAsync(cancellationToken);
        var page = (int)Math.Min(input.Page, Math.Max(1, (count + input.PageSize - 1) / input.PageSize));
        var records = await query.OrderBy(client => client.DisplayName ?? client.ClientId).ThenBy(client => client.Id)
            .Skip((page - 1) * input.PageSize).Take(input.PageSize).ToListAsync(cancellationToken);
        var items = new List<AdminClientSummary>();
        foreach (var client in records) items.Add(await SummaryAsync(client, cancellationToken));
        return new(items, count, page, input.PageSize);
    }

    public async Task<AdminClientDetail?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var client = await Records.AsNoTracking().SingleOrDefaultAsync(client => client.Id == id, cancellationToken);
        if (client is null) return null;
        return new(await SummaryAsync(client, cancellationToken), client.ApplicationType, client.ConsentType,
            await applications.GetRedirectUrisAsync(client, cancellationToken), await applications.GetPostLogoutRedirectUrisAsync(client, cancellationToken),
            await applications.HasPermissionAsync(client, Permissions.GrantTypes.RefreshToken, cancellationToken),
            await CanManageAsync(client, cancellationToken), AdminUserVersion.For(client.Id!, client.ConcurrencyToken));
    }

    private async Task<AdminClientSummary> SummaryAsync(OpenIddictEntityFrameworkCoreApplication client, CancellationToken cancellationToken) =>
        new(client.Id!, client.ClientId, client.DisplayName, client.ClientType, !string.IsNullOrEmpty(client.ClientSecret),
            await applications.HasRequirementAsync(client, Requirements.Features.ProofKeyForCodeExchange, cancellationToken),
            (await applications.GetPermissionsAsync(client, cancellationToken)).Where(value => value.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal))
                .Select(value => value[Permissions.Prefixes.Scope.Length..]).Order(StringComparer.Ordinal).ToArray());

    private async Task<bool> CanManageAsync(OpenIddictEntityFrameworkCoreApplication client, CancellationToken cancellationToken) =>
        client.ClientType is ClientTypes.Public or ClientTypes.Confidential && client.ConsentType == ConsentTypes.Implicit &&
        await applications.HasPermissionAsync(client, Permissions.GrantTypes.AuthorizationCode, cancellationToken) &&
        await applications.HasPermissionAsync(client, Permissions.ResponseTypes.Code, cancellationToken);

    public async Task<AdminMutationOutcome> CreateAsync(ClaimsPrincipal actor, AdminCreateClientInput input, CancellationToken cancellationToken) =>
        await WriteAsync(actor, null, null, input, null, null, cancellationToken);
    public async Task<AdminMutationOutcome> EditAsync(ClaimsPrincipal actor, string id, AdminEditClientInput input, CancellationToken cancellationToken) =>
        await WriteAsync(actor, id, input.Version, input, null, null, cancellationToken);
    public async Task<AdminMutationOutcome> CredentialsAsync(ClaimsPrincipal actor, string id, AdminClientCredentialsInput input, CancellationToken cancellationToken) =>
        await WriteAsync(actor, id, input.Version, null, input, null, cancellationToken);
    public async Task<AdminMutationOutcome> DeleteAsync(ClaimsPrincipal actor, string id, AdminDeleteUserInput input, CancellationToken cancellationToken) =>
        await WriteAsync(actor, id, input.Version, null, null, input.Confirmation, cancellationToken);

    private async Task<AdminMutationOutcome> WriteAsync(ClaimsPrincipal actor, string? id, string? version, AdminClientSettingsInput? input,
        AdminClientCredentialsInput? credentials, string? confirmation, CancellationToken cancellationToken)
    {
        await using var transaction = await guard.BeginAsync(cancellationToken);
        if (await guard.CheckActorAsync(actor, cancellationToken) is { } denied) return denied;
        var record = id is null ? null : await Records.SingleOrDefaultAsync(client => client.Id == id, cancellationToken);
        if (id is not null && record is null) return new(404, Error: "This client no longer exists.");
        if (record is not null && AdminUserVersion.For(record.Id!, record.ConcurrencyToken) != version) return Conflict();
        var deleting = confirmation is not null;
        if (!deleting && record is not null && !await CanManageAsync(record, cancellationToken))
            return Invalid("This imported client uses a client type, consent mode or flow outside the supported authorization-code configuration. Its settings are read-only.");
        var operation = deleting ? "client-delete" : credentials is not null ? "client-credentials" : record is null ? "client-create" : "client-edit";
        try
        {
            if (deleting)
            {
                if (confirmation != (record!.ClientId ?? record.Id)) return Invalid("Type the client ID to confirm deletion.");
                // The manager deletes associated authorizations/tokens within this same database transaction.
                await applications.DeleteAsync(record, cancellationToken);
            }
            else
            {
                var descriptor = new OpenIddictApplicationDescriptor();
                if (record is not null) await applications.PopulateAsync(descriptor, record, cancellationToken);
                else
                {
                    descriptor.ApplicationType = ApplicationTypes.Web;
                    descriptor.ConsentType = ConsentTypes.Implicit;
                    descriptor.Permissions.UnionWith([Permissions.GrantTypes.AuthorizationCode, Permissions.ResponseTypes.Code]);
                }
                var revoke = credentials is not null;
                if (credentials is not null)
                {
                    if (credentials.Confirmation != record!.ClientId) return Invalid("Type the client ID to confirm the credential change.");
                    if (ValidateCredentials(credentials.ClientType, credentials.ClientSecret) is { } error) return Invalid(error);
                    descriptor.ClientType = credentials.ClientType;
                    descriptor.ClientSecret = credentials.ClientType == ClientTypes.Public ? null : credentials.ClientSecret;
                    if (credentials.ClientType == ClientTypes.Public) descriptor.Requirements.Add(Requirements.Features.ProofKeyForCodeExchange);
                }
                else
                {
                    if (input is AdminCreateClientInput create)
                    {
                        if (ValidateCredentials(create.ClientType, create.ClientSecret) is { } error) return Invalid(error);
                        descriptor.ClientType = create.ClientType;
                        descriptor.ClientSecret = create.ClientType == ClientTypes.Public ? null : create.ClientSecret;
                    }
                    if (await ValidateSettingsAsync(input!, descriptor, record is not null, cancellationToken) is { } invalid) return Invalid(invalid);
                    var clientId = input!.ClientId.Trim();
                    if (record is not null && clientId != record.ClientId) return Invalid("Client IDs cannot be changed. Register a separate client for a new ID.");
                    if (await Records.AnyAsync(client => client.ClientId == clientId && client.Id != id, cancellationToken)) return Invalid("A client already uses this ID.");
                    var redirects = input.RedirectUris.Select(value => new Uri(value.Trim(), UriKind.Absolute)).ToHashSet();
                    var logout = input.PostLogoutRedirectUris.Select(value => new Uri(value.Trim(), UriKind.Absolute)).ToHashSet();
                    var requestedScopes = input.Scopes.Select(value => Permissions.Prefixes.Scope + value.Trim()).ToHashSet(StringComparer.Ordinal);
                    revoke = record is not null && (!descriptor.RedirectUris.SetEquals(redirects) || !descriptor.PostLogoutRedirectUris.SetEquals(logout) ||
                        !descriptor.Permissions.Where(value => value.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal)).ToHashSet(StringComparer.Ordinal).SetEquals(requestedScopes) ||
                        descriptor.Permissions.Contains(Permissions.GrantTypes.RefreshToken) != input.AllowRefreshTokens ||
                        descriptor.Requirements.Contains(Requirements.Features.ProofKeyForCodeExchange) != input.RequirePkce);
                    descriptor.ClientId = clientId;
                    descriptor.DisplayName = input.DisplayName.Trim();
                    descriptor.RedirectUris.Clear(); descriptor.RedirectUris.UnionWith(redirects);
                    descriptor.PostLogoutRedirectUris.Clear(); descriptor.PostLogoutRedirectUris.UnionWith(logout);
                    descriptor.Permissions.RemoveWhere(value => value.StartsWith(Permissions.Prefixes.Scope, StringComparison.Ordinal));
                    descriptor.Permissions.UnionWith(requestedScopes);
                    Set(descriptor.Permissions, Permissions.GrantTypes.RefreshToken, input.AllowRefreshTokens);
                    Set(descriptor.Requirements, Requirements.Features.ProofKeyForCodeExchange, input.RequirePkce);
                }
                // Populating the descriptor keeps the existing hash and all unrelated metadata intact.
                // OpenIddict hashes only a changed secret; plaintext is never returned to the browser.
                if (record is null) id = await applications.GetIdAsync(await applications.CreateAsync(descriptor, cancellationToken), cancellationToken);
                else await applications.UpdateAsync(record, descriptor, cancellationToken);
                if (revoke)
                {
                    await tokens.RevokeByApplicationIdAsync(id!, cancellationToken);
                    await authorizations.RevokeByApplicationIdAsync(id!, cancellationToken);
                }
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OpenIddictExceptions.ConcurrencyException) { return Conflict(); }
        catch (OpenIddictExceptions.ValidationException) { return Invalid("The client settings could not be saved. Check its ID, URLs and credential configuration, then reload before retrying."); }
        logger.LogInformation("Administrator {ActorId} performed {Operation} on client {ClientRecordId}", actor.FindFirstValue(Claims.Subject), operation, id);
        return new(200, id);
    }

    private async Task<string?> ValidateSettingsAsync(AdminClientSettingsInput input, OpenIddictApplicationDescriptor previous, bool existing, CancellationToken cancellationToken)
    {
        if (input.ClientId.Trim().Length == 0 || input.ClientId.Trim().Any(c => char.IsWhiteSpace(c) || char.IsControl(c))) return "Use a client ID without whitespace or control characters.";
        if (previous.ClientType == ClientTypes.Public && !input.RequirePkce) return "Public clients require PKCE. Enable it before saving this client.";
        foreach (var (values, old) in new[] { (input.RedirectUris, previous.RedirectUris), (input.PostLogoutRedirectUris, previous.PostLogoutRedirectUris) })
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var value in values)
            {
                if (string.IsNullOrWhiteSpace(value) || value.Length > 2048 || !Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) || !uri.IsWellFormedOriginalString())
                    return "Enter valid absolute redirect URLs of at most 2048 characters.";
                if (!seen.Add(uri.AbsoluteUri)) return "List each redirect URL only once.";
                // Keep existing AIS2 callbacks unchanged, including native/custom schemes. New URLs use the supported web profile.
                if (existing && old.Any(item => item.AbsoluteUri == uri.AbsoluteUri)) continue;
                if (value.Any(char.IsControl) || value.Contains('*') || uri.Fragment.Length != 0 || uri.UserInfo.Length != 0 ||
                    string.IsNullOrEmpty(uri.Host) || (uri.Scheme != "https" && !(uri.Scheme == "http" &&
                    (uri.Host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IPAddress.TryParse(uri.IdnHost.Trim('[', ']'), out var address) && IPAddress.IsLoopback(address)))))
                    return "New redirect URLs must use HTTPS (HTTP is allowed only on localhost or a loopback IP), without credentials, wildcards or fragments.";
            }
        }
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var value in input.Scopes)
        {
            if (string.IsNullOrWhiteSpace(value) || value.Length > 200 || value.Any(c => c < '!' || c > '~' || c is '"' or '\\')) return "Enter valid scope names.";
            var name = value.Trim();
            if (!names.Add(name)) return "List each scope only once.";
            if (AisConstants.StandardScopes.ContainsKey(name) || name == Scopes.OfflineAccess ||
                previous.Permissions.Contains(Permissions.Prefixes.Scope + name) || await scopes.FindByNameAsync(name, cancellationToken) is not null) continue;
            return "A requested scope is not defined. Create it on the Scopes page first.";
        }
        return null;
    }

    private static string? ValidateCredentials(string type, string? secret) => type == ClientTypes.Public
        ? string.IsNullOrEmpty(secret) ? null : "Public clients cannot have a secret."
        : secret is null || secret.Length < 32 || secret.Length > 256 || secret.Any(c => c < '!' || c > '~')
            ? "Use a new client secret of 32–256 printable ASCII characters without spaces. The Generate button creates a random secret." : null;
    private static void Set(HashSet<string> values, string name, bool enabled) { if (enabled) values.Add(name); else values.Remove(name); }
    private static AdminMutationOutcome Invalid(string message) => new(400, Error: message);
    private static AdminMutationOutcome Conflict() => new(409, Error: "This client has changed. Reload its details before trying again.");
}
