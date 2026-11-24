using System.Data;
using System.Security.Claims;
using System.Text.Json;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using Microsoft.EntityFrameworkCore;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Services;

public sealed class AdminScopeService(AisDbContext database, AdminWriteGuard guard, IOpenIddictScopeManager scopes,
    IOpenIddictApplicationManager applications, IOpenIddictAuthorizationManager authorizations, ILogger<AdminScopeService> logger)
{
    private IQueryable<OpenIddictEntityFrameworkCoreScope> Records => database.Set<OpenIddictEntityFrameworkCoreScope>();
    public static bool Standard(string? name) => name is not null && AisConstants.StandardScopeNames.Contains(name);

    public async Task<AdminScopePage> SearchAsync(AdminScopeQuery input, CancellationToken cancellationToken)
    {
        await using var transaction = await database.Database.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var query = Records.AsNoTracking();
        if (!string.IsNullOrWhiteSpace(input.Q))
        {
            var pattern = "%" + input.Q.Trim().Replace("\\", "\\\\").Replace("%", "\\%").Replace("_", "\\_") + "%";
            query = query.Where(scope => EF.Functions.ILike((scope.Name ?? "") + " " + (scope.DisplayName ?? ""), pattern, "\\"));
        }
        var count = await query.LongCountAsync(cancellationToken);
        var page = (int)Math.Min(input.Page, Math.Max(1, (count + input.PageSize - 1) / input.PageSize));
        var records = await query.OrderBy(scope => scope.Name != null && AisConstants.StandardScopeNames.Contains(scope.Name))
            .ThenBy(scope => scope.Name).ThenBy(scope => scope.Id)
            .Skip((page - 1) * input.PageSize).Take(input.PageSize).ToListAsync(cancellationToken);
        var items = new List<AdminScopeDetail>();
        foreach (var scope in records) items.Add(await DetailAsync(scope, cancellationToken));
        return new(items, count, page, input.PageSize);
    }

    public async Task<AdminScopeDetail?> FindAsync(string id, CancellationToken cancellationToken)
    {
        var scope = await Records.AsNoTracking().SingleOrDefaultAsync(scope => scope.Id == id, cancellationToken);
        return scope is null ? null : await DetailAsync(scope, cancellationToken);
    }

    private async Task<AdminScopeDetail> DetailAsync(OpenIddictEntityFrameworkCoreScope scope, CancellationToken cancellationToken)
    {
        var properties = await scopes.GetPropertiesAsync(scope, cancellationToken);
        // Standard token mappings are fixed by AIS2's protocol contract, not this JSON property.
        var claims = scope.Name is not null && AisConstants.StandardScopes.TryGetValue(scope.Name, out var standard) ? standard :
            properties.TryGetValue("claims", out var value) && value.ValueKind == JsonValueKind.Array
                ? value.EnumerateArray().Where(item => item.ValueKind == JsonValueKind.String).Select(item => item.GetString()!).ToArray() : [];
        return new(scope.Id!, scope.Name, scope.DisplayName, scope.Description, claims,
            await scopes.GetResourcesAsync(scope, cancellationToken), Standard(scope.Name), AdminUserVersion.For(scope.Id!, scope.ConcurrencyToken));
    }

    public async Task<AdminMutationOutcome> CreateAsync(ClaimsPrincipal actor, AdminScopeInput input, CancellationToken cancellationToken) =>
        await WriteAsync(actor, null, null, input, null, cancellationToken);

    public async Task<AdminMutationOutcome> EditAsync(ClaimsPrincipal actor, string id, AdminEditScopeInput input, CancellationToken cancellationToken) =>
        await WriteAsync(actor, id, input.Version, input, null, cancellationToken);

    public async Task<AdminMutationOutcome> DeleteAsync(ClaimsPrincipal actor, string id, AdminDeleteUserInput input, CancellationToken cancellationToken) =>
        await WriteAsync(actor, id, input.Version, null, input.Confirmation, cancellationToken);

    private async Task<AdminMutationOutcome> WriteAsync(ClaimsPrincipal actor, string? id, string? version, AdminScopeInput? input,
        string? confirmation, CancellationToken cancellationToken)
    {
        await using var transaction = await guard.BeginAsync(cancellationToken);
        if (await guard.CheckActorAsync(actor, cancellationToken) is { } denied) return denied;
        var record = id is null ? null : await Records.SingleOrDefaultAsync(scope => scope.Id == id, cancellationToken);
        if (id is not null && record is null) return new(404, Error: "This scope no longer exists.");
        if (record is not null && AdminUserVersion.For(record.Id!, record.ConcurrencyToken) != version) return Conflict();
        if (record is not null && Standard(record.Name)) return new(400, Error: "Standard scopes are managed by the service.");
        var descriptionChanged = input is not null && record?.Description != input.Description?.Trim();
        try
        {
            if (input is null)
            {
                if (confirmation != (record!.Name ?? record.Id)) return new(400, Error: "Type the scope name to confirm deletion.");
                // Do not silently break clients or strand grants. Client editing remains Step 11.
                await foreach (var app in applications.ListAsync(cancellationToken: cancellationToken))
                    if (await applications.HasPermissionAsync(app, Permissions.Prefixes.Scope + record.Name, cancellationToken))
                        return new(400, Error: "A registered client allows this scope. Remove that permission before deleting it.");
                await foreach (var authorization in authorizations.FindAsync(subject: null, client: null, type: null, status: Statuses.Valid, scopes: [record.Name!], cancellationToken: cancellationToken))
                    return new(400, Error: "An active authorization uses this scope. Revoke its grants before deleting it.");
                await scopes.DeleteAsync(record, cancellationToken);
            }
            else
            {
                if (Validate(input) is { } error) return new(400, Error: error);
                var name = input.Name.Trim();
                if (Standard(name)) return new(400, Error: "Standard scopes are managed by the service.");
                if (record is not null && name != record.Name) return new(400, Error: "Scope names cannot be changed. Create a separate scope for a new name.");
                if (await Records.AnyAsync(scope => scope.Name == name && scope.Id != id, cancellationToken))
                    return new(400, Error: "A scope already uses this name.");
                var descriptor = new OpenIddictScopeDescriptor();
                // Keep imported localized names/descriptions and unrelated properties intact.
                if (record is not null) await scopes.PopulateAsync(descriptor, record, cancellationToken);
                descriptor.Name = name;
                descriptor.DisplayName = input.DisplayName?.Trim();
                descriptor.Description = input.Description?.Trim();
                descriptor.Properties["claims"] = JsonSerializer.SerializeToElement(input.Claims.Select(value => value.Trim()));
                descriptor.Resources.Clear();
                descriptor.Resources.UnionWith(input.Resources.Select(value => value.Trim()));
                if (record is null) id = await scopes.GetIdAsync(await scopes.CreateAsync(descriptor, cancellationToken), cancellationToken);
                else await scopes.UpdateAsync(record, descriptor, cancellationToken);
            }
            await transaction.CommitAsync(cancellationToken);
        }
        catch (OpenIddictExceptions.ConcurrencyException) { return Conflict(); }
        catch (OpenIddictExceptions.ValidationException) { return new(400, Error: "The scope is invalid or its name is already in use. Reload and check the fields."); }
        var operation = input is null ? "deleted" : record is null ? "created" : "updated";
        OperationLog.Admin(logger, actor, operation, "scope", record?.Name ?? input?.Name.Trim(), id, input is null ? null : new
        {
            DisplayName = OperationLog.Text(input.DisplayName?.Trim()),
            DescriptionChanged = descriptionChanged,
            ClaimTypes = input.Claims.Select(value => value.Trim()).ToArray(),
            Resources = input.Resources.Select(OperationLog.ScopeResource).ToArray()
        });
        return new(200, id);
    }

    private static string? Validate(AdminScopeInput input)
    {
        var name = input.Name.Trim();
        if (name.Length == 0 || name.Any(c => c < '!' || c > '~' || c is '"' or '\\'))
            return "Use a scope name without spaces, quotes, backslashes or non-ASCII characters.";
        if (input.Claims.Any(value => value is null || !AdminClaimRules.ValidType(value.Trim()) || AdminClaimRules.ProtocolOrSecret(value.Trim())))
            return "Claims must be valid claim types; private security and protocol claims cannot be exposed.";
        if (input.Resources.Any(value => string.IsNullOrWhiteSpace(value) || value.Length > 2000 || value.Any(char.IsWhiteSpace) || value.Any(char.IsControl)))
            return "Resources must be nonempty identifiers of at most 2000 characters, without spaces or control characters.";
        if (input.Claims.Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Count() != input.Claims.Length ||
            input.Resources.Select(value => value.Trim()).Distinct(StringComparer.Ordinal).Count() != input.Resources.Length)
            return "List each claim type and resource only once.";
        return null;
    }

    private static AdminMutationOutcome Conflict() => new(409, Error: "This scope has changed. Reload it before trying again.");
}
