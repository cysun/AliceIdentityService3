using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AliceIdentityService.Services;
using AliceIdentityService.Models;
using AliceIdentityService.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;

namespace AliceIdentityService.Tests;

[Collection("PostgreSQL")]
public sealed class PersistenceTests
{
    private const string Password = "Test-only-Strong-Password-42!";
    private static readonly AdministratorRequest Administrator = new("admin@example.test", "Sample", "Administrator");

    [PostgresFact]
    public async Task FreshSqlScriptsMatchTheModelWithoutRequiringLegacySearchObjects()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var readiness = new DatabaseReadinessCheck(database.Services.GetRequiredService<IServiceScopeFactory>());
        Assert.Equal(HealthStatus.Unhealthy, (await readiness.CheckHealthAsync(new HealthCheckContext())).Status);
        await database.InitializeSchemaAsync();
        var inspector = new SchemaInspector(database.Db);
        var report = await inspector.CheckAsync(includeLegacySearchObjects: false);
        Assert.True(report.Compatible, string.Join("\n", report.Differences));
        var catalog = await inspector.ReadCatalogAsync();
        Assert.DoesNotContain(catalog, entry => entry.Kind == "function");
        Assert.DoesNotContain(catalog, entry => entry.Kind == "index" && entry.Key.Contains("AspNetUsers_lower_idx", StringComparison.Ordinal));
        Assert.Empty(await database.Db.Users.ToListAsync());
        AssertModelMatchesCatalog(database.Db, catalog);
        Assert.Equal(HealthStatus.Healthy, (await readiness.CheckHealthAsync(new HealthCheckContext())).Status);
    }

    [PostgresFact]
    public async Task BootstrapRefusesAnUninitializedDatabaseBeforeRequestingAPassword()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        var error = await Assert.ThrowsAsync<PostgresException>(() => database.Services.GetRequiredService<BootstrapService>()
            .RunAsync(Administrator, () => throw new InvalidOperationException("Must not request a password")));
        Assert.Equal(PostgresErrorCodes.UndefinedTable, error.SqlState);
        Assert.Empty(await new SchemaInspector(database.Db).ReadCatalogAsync());
    }

    [PostgresFact]
    public async Task BootstrapIsAtomicAndDoesNotResetAnExistingAdministratorOrScope()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        await database.Db.Database.ExecuteSqlRawAsync("DROP TABLE \"__EFMigrationsHistory\"");
        var scopes = database.Services.GetRequiredService<IOpenIddictScopeManager>();
        var customScope = new OpenIddictScopeDescriptor { Name = "profile", DisplayName = "Existing profile scope" };
        customScope.Properties["claims"] = JsonSerializer.SerializeToElement(new[] { "custom_claim" });
        await scopes.CreateAsync(customScope);
        var bootstrap = database.Services.GetRequiredService<BootstrapService>();
        var first = await bootstrap.RunAsync(Administrator, () => Task.FromResult(Password));
        Assert.True(first.AdministratorCreated);
        Assert.Equal(4, first.ScopesCreated);
        var users = database.Services.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByEmailAsync(Administrator.Email))!;
        Assert.True(user.EmailConfirmed);
        Assert.Contains(await users.GetClaimsAsync(user), claim => claim.Type == AisConstants.AdminClaim);
        Assert.NotEqual(PasswordVerificationResult.Failed, users.PasswordHasher.VerifyHashedPassword(user, user.PasswordHash!, Password));
        var hash = user.PasswordHash;
        var stamp = user.SecurityStamp;
        var second = await bootstrap.RunAsync(Administrator with { FirstName = "Different" }, () => throw new InvalidOperationException("Must not request another password"));
        Assert.False(second.AdministratorCreated);
        Assert.Equal(0, second.ScopesCreated);
        Assert.Equal(hash, user.PasswordHash);
        Assert.Equal(stamp, user.SecurityStamp);
        Assert.Equal("Sample", user.FirstName);
        var profile = (await scopes.FindByNameAsync("profile"))!;
        Assert.Equal("Existing profile scope", await scopes.GetDisplayNameAsync(profile));
        Assert.Equal("custom_claim", (await scopes.GetPropertiesAsync(profile))["claims"][0].GetString());
        Assert.Equal(5, await scopes.CountAsync());
    }

    [PostgresFact]
    public async Task FailedBootstrapLeavesNoUserClaimOrScopeAndDoesNotPromoteExistingUsers()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        var bootstrap = database.Services.GetRequiredService<BootstrapService>();
        await Assert.ThrowsAsync<InvalidOperationException>(() => bootstrap.RunAsync(Administrator, () => Task.FromResult("weak")));
        Assert.Empty(await database.Db.Users.ToListAsync());
        Assert.Empty(await database.Db.UserClaims.ToListAsync());
        Assert.Empty(await database.Db.Set<OpenIddictEntityFrameworkCoreScope>().ToListAsync());
        database.Db.ChangeTracker.Clear();
        var users = database.Services.GetRequiredService<UserManager<User>>();
        var existing = new User { UserName = Administrator.Email, Email = Administrator.Email, FirstName = "Existing", LastName = "User", ScreenName = "Existing User" };
        Assert.True((await users.CreateAsync(existing, Password)).Succeeded);
        var hash = existing.PasswordHash;
        await Assert.ThrowsAsync<InvalidOperationException>(() => bootstrap.RunAsync(Administrator, () => Task.FromResult(Password)));
        Assert.Equal(hash, existing.PasswordHash);
        Assert.False(existing.EmailConfirmed);
        Assert.Empty(await users.GetClaimsAsync(existing));
    }

    [PostgresFact]
    public async Task OpenIddictStoresRoundTripClientsAuthorizationsAndTokens()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        var applications = database.Services.GetRequiredService<IOpenIddictApplicationManager>();
        var descriptor = new OpenIddictApplicationDescriptor { ClientId = "sample-client", ClientSecret = Password, DisplayName = "Sample client", ClientType = "confidential", ConsentType = "implicit" };
        descriptor.RedirectUris.Add(new Uri("https://client.example.test/callback"));
        descriptor.Permissions.Add("scp:profile");
        var application = await applications.CreateAsync(descriptor);
        Assert.True(await applications.ValidateClientSecretAsync(application, Password));
        Assert.Contains("https://client.example.test/callback", await applications.GetRedirectUrisAsync(application));
        var authorization = new OpenIddictEntityFrameworkCoreAuthorization { Id = Guid.NewGuid().ToString(), Application = (OpenIddictEntityFrameworkCoreApplication)application, Status = "valid", Type = "permanent", Subject = "sample-subject", CreationDate = DateTime.UtcNow };
        database.Db.Add(authorization);
        database.Db.Add(new OpenIddictEntityFrameworkCoreToken { Id = Guid.NewGuid().ToString(), Application = (OpenIddictEntityFrameworkCoreApplication)application, Authorization = authorization, Status = "valid", Type = "refresh_token", Subject = "sample-subject", CreationDate = DateTime.UtcNow, ExpirationDate = DateTime.UtcNow.AddDays(1) });
        await database.Db.SaveChangesAsync();
        Assert.Equal(1, await database.Services.GetRequiredService<IOpenIddictAuthorizationManager>().CountAsync());
        Assert.Equal(1, await database.Services.GetRequiredService<IOpenIddictTokenManager>().CountAsync());
    }

    [RestoredDatabaseFact]
    public async Task RestoredAis2DataCanBeReadWithoutSchemaOrDataChanges()
    {
        await using var provider = PostgresTestDatabase.ServicesFor(Environment.GetEnvironmentVariable("AIS3_TEST_RESTORED_CONNECTION")!)
            .BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        await using var scope = provider.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AisDbContext>();
        await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
        await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
        var inspector = new SchemaInspector(db);
        Assert.True((await inspector.CheckAsync()).Compatible);
        AssertModelMatchesCatalog(db, await inspector.ReadCatalogAsync());
        Assert.NotEmpty(await db.Users.AsNoTracking().ToListAsync());
        _ = await db.Roles.AsNoTracking().ToListAsync();
        _ = await db.RoleClaims.AsNoTracking().ToListAsync();
        _ = await db.UserClaims.AsNoTracking().ToListAsync();
        _ = await db.UserLogins.AsNoTracking().ToListAsync();
        _ = await db.UserRoles.AsNoTracking().ToListAsync();
        _ = await db.UserTokens.AsNoTracking().ToListAsync();
        _ = await db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking().ToListAsync();
        _ = await db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().ToListAsync();
        var clients = scope.ServiceProvider.GetRequiredService<IOpenIddictApplicationManager>();
        await foreach (var client in clients.ListAsync())
        {
            _ = await clients.GetPermissionsAsync(client);
            _ = await clients.GetRedirectUrisAsync(client);
            _ = await clients.GetPropertiesAsync(client);
        }
        var scopes = scope.ServiceProvider.GetRequiredService<IOpenIddictScopeManager>();
        await foreach (var item in scopes.ListAsync()) _ = await scopes.GetPropertiesAsync(item);
        await transaction.CommitAsync();
    }

    [Fact]
    public void IdentityRecognizesAnAis2CompatibleV3PasswordHash()
    {
        // Identity V3 binary format, with the legacy 100,000-iteration SHA-512 policy.
        var salt = Enumerable.Range(1, 16).Select(i => (byte)i).ToArray();
        var payload = new byte[13 + salt.Length + 32];
        payload[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(1), 2);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(5), 100_000);
        BinaryPrimitives.WriteUInt32BigEndian(payload.AsSpan(9), (uint)salt.Length);
        salt.CopyTo(payload, 13);
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(Password), salt, 100_000, HashAlgorithmName.SHA512, 32).CopyTo(payload, 29);
        var hash = Convert.ToBase64String(payload);
        Assert.NotEqual(PasswordVerificationResult.Failed, new PasswordHasher<User>().VerifyHashedPassword(new User(), hash, Password));
    }

    private static void AssertModelMatchesCatalog(AisDbContext db, IReadOnlyList<SchemaEntry> entries)
    {
        var columns = entries.Where(entry => entry.Kind == "column").ToDictionary(entry => entry.Key);
        foreach (var table in db.Model.GetRelationalModel().Tables)
            foreach (var column in table.Columns)
            {
                Assert.True(columns.TryGetValue($"{table.Name}.{column.Name}", out var entry), $"Missing mapped column: {table.Name}.{column.Name}");
                using var definition = JsonDocument.Parse(entry!.Definition);
                Assert.Equal(definition.RootElement[0].GetString(), column.StoreType);
                Assert.Equal(definition.RootElement[1].GetBoolean(), column.IsNullable);
            }
    }
}
