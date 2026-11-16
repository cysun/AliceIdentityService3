using System.Buffers.Binary;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AliceIdentityService.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using OpenIddict.Abstractions;
using OpenIddict.EntityFrameworkCore.Models;
using OpenIddict.Server;
using static OpenIddict.Abstractions.OpenIddictConstants;

namespace AliceIdentityService.Tests;

[Collection("PostgreSQL")]
public sealed class MigrationTests
{
    [MigrationSourceFact]
    public async Task Ais2SnapshotRestoresWithoutDataChangesAndRollbackRestoresTheSameArchive()
    {
        var connection = Environment.GetEnvironmentVariable("AIS3_TEST_MIGRATION_SOURCE_CONNECTION")!;
        await using var rehearsal = new MigrationRehearsal();
        var checks = new List<string>();
        var passed = false;
        try
        {
            await rehearsal.CaptureAsync(connection);
            checks.Add("Read-only source snapshot and pg_dump captured together; sequences stable during capture.");
            await using var candidate = await PostgresTestDatabase.CreateAsync();
            await rehearsal.RestoreAsync(candidate);
            checks.Add("All public table counts/content digests, sequences and shared catalog match restored archive.");
            await PrepareAndRead(candidate);
            MigrationRehearsal.RequireEqual(rehearsal.Source, await MigrationRehearsal.FingerprintAsync(candidate));
            checks.Add("PopulateSchema applied twice; AIS3 schema/store reads passed without changing source data/history.");
            await using var rollback = await PostgresTestDatabase.CreateAsync();
            await rehearsal.RestoreAsync(rollback);
            checks.Add("Second independent restore from the same archive matches the baseline (pre-write rollback rehearsal).");
            MigrationRehearsal.RequireEqual(rehearsal.Source, await MigrationRehearsal.FingerprintConnectionAsync(connection));
            checks.Add("Fresh read-only source fingerprint still matches after rehearsal.");
            passed = true;
        }
        finally { await rehearsal.WriteReportAsync("ais2", checks, passed); }
    }

    [PostgresFact]
    public async Task DumpedLegacyProfilesSignInAndCompletePublicAndConfidentialOidcFlows()
    {
        // Synthetic AIS2-compatible records, not real-user credentials. First persist into a
        // separate source, then exercise only the pg_dump/pg_restore copy with AIS3.
        await using var source = await OidcTestClient.Create();
        var users = source.Database.Services.GetRequiredService<UserManager<User>>();
        var user = (await users.FindByEmailAsync(OidcTestClient.Email))!;
        var oldHash = LegacyPasswordHash(OidcTestClient.Password);
        user.PasswordHash = oldHash;
        Assert.True((await users.UpdateAsync(user)).Succeeded);
        var userId = user.Id;
        var securityStamp = user.SecurityStamp;
        foreach (var state in new[] { "unconfirmed", "locked", "twofactor" })
        {
            var account = new User
            {
                UserName = state + "@example.test",
                Email = state + "@example.test",
                FirstName = "Imported",
                LastName = state,
                ScreenName = state,
                EmailConfirmed = state != "unconfirmed",
                TwoFactorEnabled = state == "twofactor",
                LockoutEnabled = true,
                LockoutEnd = state == "locked" ? DateTimeOffset.UtcNow.AddHours(1) : null
            };
            Assert.True((await users.CreateAsync(account, OidcTestClient.Password)).Succeeded);
        }
        // Nonempty old protocol rows must survive transfer even though they prove no continuity.
        var app = await source.Database.Db.Set<OpenIddictEntityFrameworkCoreApplication>().SingleAsync(a => a.ClientId == OidcTestClient.ClientId);
        var authorization = new OpenIddictEntityFrameworkCoreAuthorization
        {
            Id = "legacy-authorization",
            Application = app,
            Status = Statuses.Revoked,
            Type = AuthorizationTypes.Permanent,
            Subject = user.Id,
            Scopes = "[\"openid\"]",
            CreationDate = DateTime.UtcNow
        };
        source.Database.Db.Add(authorization);
        source.Database.Db.Add(new OpenIddictEntityFrameworkCoreToken
        {
            Id = "legacy-token",
            Application = app,
            Authorization = authorization,
            Status = Statuses.Revoked,
            Type = TokenTypeIdentifiers.RefreshToken,
            Subject = user.Id,
            Payload = "synthetic-legacy-payload",
            CreationDate = DateTime.UtcNow
        });
        await source.Database.Db.SaveChangesAsync();

        await using var rehearsal = new MigrationRehearsal();
        var checks = new List<string>();
        var passed = false;
        try
        {
            await rehearsal.CaptureAsync(source.Database.Db.Database.GetConnectionString()!);
            await using var migrated = await PostgresTestDatabase.CreateAsync();
            await rehearsal.RestoreAsync(migrated);
            await PrepareAndRead(migrated);
            MigrationRehearsal.RequireEqual(rehearsal.Source, await MigrationRehearsal.FingerprintAsync(migrated));
            checks.Add("Synthetic legacy hashes, IDs, stamps, claims, client secrets, scopes and protocol rows match before sign-in.");
            await using (var factory = new AuthenticationFactory(migrated.Db.Database.GetConnectionString()!))
            {
                using var browser = factory.Browser();
                var login = await Login(browser, OidcTestClient.Email);
                Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
                Assert.Equal(HttpStatusCode.OK, (await browser.GetAsync("/api/admin/session")).StatusCode);
                var profile = await browser.GetFromJsonAsync<JsonElement>("/api/admin/session");
                Assert.Contains(userId, profile.ToString());
                foreach (var state in new[] { "unconfirmed", "locked", "twofactor" })
                {
                    using var denied = factory.Browser();
                    Assert.Equal(HttpStatusCode.OK, (await Login(denied, state + "@example.test")).StatusCode);
                    Assert.Equal(HttpStatusCode.Unauthorized, (await denied.GetAsync("/api/admin/session")).StatusCode);
                }

                // OidcTestClient owns its factory/database when disposed; this scope owns them instead.
                var protocol = new OidcTestClient(migrated, factory);
                using (protocol.Browser)
                {
                    foreach (var confidential in new[] { false, true })
                    {
                        var clientId = confidential ? "confidential-test-client" : OidcTestClient.ClientId;
                        var secret = confidential ? OidcTestClient.Secret : null;
                        var parameters = protocol.Parameters();
                        parameters["client_id"] = clientId;
                        var code = await protocol.Code(parameters);
                        var response = await protocol.Redeem(code, clientId: clientId, secret: secret);
                        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                        var tokens = await response.Content.ReadFromJsonAsync<JsonElement>();
                        var id = await protocol.ValidateIdToken(tokens.GetProperty("id_token").GetString()!, clientId);
                        Assert.Equal(userId, id.Subject);
                        Assert.Equal("Computing", id.GetClaim("department").Value);
                        Assert.Equal(OidcTestClient.Email, id.GetClaim("name").Value);
                        Assert.Equal(HttpStatusCode.OK, (await protocol.UserInfo(tokens.GetProperty("access_token").GetString()!)).StatusCode);
                        var refresh = await protocol.Browser.PostAsync("/connect/token", new FormUrlEncodedContent(new Dictionary<string, string>
                        {
                            ["grant_type"] = "refresh_token",
                            ["refresh_token"] = tokens.GetProperty("refresh_token").GetString()!,
                            ["client_id"] = clientId,
                            ["client_secret"] = secret ?? ""
                        }));
                        Assert.Equal(HttpStatusCode.OK, refresh.StatusCode);
                    }
                    var logout = await protocol.Browser.GetStringAsync("/connect/logout");
                    Assert.Equal(HttpStatusCode.Redirect, (await protocol.Browser.PostAsync("/connect/logout", new FormUrlEncodedContent(new Dictionary<string, string>
                    {
                        ["confirm"] = "yes",
                        ["__RequestVerificationToken"] = OidcTestClient.Field(logout, "__RequestVerificationToken")
                    }))).StatusCode);
                    Assert.Equal(HttpStatusCode.Unauthorized, (await protocol.Browser.GetAsync("/api/admin/session")).StatusCode);
                }
            }
            var after = await migrated.Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId);
            Assert.NotEqual(securityStamp, after.SecurityStamp);
            Assert.NotEqual(oldHash, after.PasswordHash); // Identity rehash also rotates the security stamp.
            Assert.NotEqual(PasswordVerificationResult.Failed, users.PasswordHasher.VerifyHashedPassword(after, after.PasswordHash!, OidcTestClient.Password));
            checks.Add("Imported administrator login, denied account states, public S256/confidential code, signed claims, userinfo, refresh and logout passed.");
            await using var rollback = await PostgresTestDatabase.CreateAsync();
            await rehearsal.RestoreAsync(rollback);
            Assert.Equal(oldHash, (await rollback.Db.Users.AsNoTracking().SingleAsync(u => u.Id == userId)).PasswordHash);
            MigrationRehearsal.RequireEqual(rehearsal.Source, await MigrationRehearsal.FingerprintAsync(source.Database));
            checks.Add("Baseline archive restore retains original hash after candidate writes; source remains unchanged. Candidate writes are intentionally disposable.");
            passed = true;
        }
        finally { await rehearsal.WriteReportAsync("synthetic", checks, passed); }
    }

    [PostgresFact]
    public async Task LegacyGrantsWithoutAuthenticationTimeRequireSignInEvenWithMatchingKeys()
    {
        // AIS2's principal builder does not add auth_time. Simulate that claim contract while
        // retaining valid signatures, encryption, stored grants, user stamps and client settings.
        // This is intentionally not presented as a token issued by a deployed AIS2 server.
        var stripCode = true;
        await using var client = await OidcTestClient.Create(services => services.Configure<OpenIddictServerOptions>(options =>
            options.Handlers.Add(OpenIddictServerHandlerDescriptor.CreateBuilder<OpenIddictServerEvents.ProcessSignInContext>()
                .SetOrder(OpenIddictServerHandlers.PrepareIdentityTokenPrincipal.Descriptor.Order + 500)
                .UseInlineHandler(context =>
                {
                    if (stripCode) context.AuthorizationCodePrincipal?.SetClaim(Claims.AuthenticationTime, (string?)null);
                    context.RefreshTokenPrincipal?.SetClaim(Claims.AuthenticationTime, (string?)null);
                    return default;
                }).Build())));
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Redeem(await client.Code())));
        stripCode = false;
        var tokens = await client.Tokens();
        Assert.Equal(Errors.InvalidGrant, await OidcTestClient.Error(await client.Refresh(tokens.GetProperty("refresh_token").GetString()!)));
    }

    [PostgresFact]
    public async Task ValidationDetectsSameCountHashChangesSequenceChangesAndSchemaChanges()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        var users = database.Services.GetRequiredService<UserManager<User>>();
        Assert.True((await users.CreateAsync(new User { UserName = "fixture", FirstName = "Test", LastName = "User", ScreenName = "Test" }, OidcTestClient.Password)).Succeeded);
        var before = await MigrationRehearsal.FingerprintAsync(database);
        await database.Db.Database.ExecuteSqlRawAsync("UPDATE \"AspNetUsers\" SET \"PasswordHash\" = 'changed'");
        var after = await MigrationRehearsal.FingerprintAsync(database);
        Assert.Equal(before.Tables.Sum(t => t.Rows), after.Tables.Sum(t => t.Rows));
        Assert.Throws<InvalidOperationException>(() => MigrationRehearsal.RequireEqual(before, after));
        before = after;
        await database.Db.Database.ExecuteSqlRawAsync("SELECT nextval('\"AspNetUserClaims_Id_seq\"')");
        after = await MigrationRehearsal.FingerprintAsync(database);
        Assert.Throws<InvalidOperationException>(() => MigrationRehearsal.RequireEqual(before, after));
        before = after;
        await database.Db.Database.ExecuteSqlRawAsync("DROP INDEX \"EmailIndex\"");
        after = await MigrationRehearsal.FingerprintAsync(database);
        Assert.Throws<InvalidOperationException>(() => MigrationRehearsal.RequireEqual(before, after));
        await using var rehearsal = new MigrationRehearsal();
        await Assert.ThrowsAsync<InvalidOperationException>(() => rehearsal.RestoreAsync(database));
    }

    private static async Task PrepareAndRead(PostgresTestDatabase database)
    {
        await database.Db.Database.ExecuteSqlRawAsync(SqlResources.Read("PopulateSchema.sql", "Scripts"));
        await database.Db.Database.ExecuteSqlRawAsync(SqlResources.Read("PopulateSchema.sql", "Scripts"));
        var schema = await new SchemaInspector(database.Db).CheckAsync(includeLegacySearchObjects: false);
        Assert.True(schema.Compatible, string.Join("\n", schema.Differences));
        _ = await database.Db.Users.AsNoTracking().ToListAsync();
        _ = await database.Db.UserClaims.AsNoTracking().ToListAsync();
        _ = await database.Db.Set<OpenIddictEntityFrameworkCoreAuthorization>().AsNoTracking().ToListAsync();
        _ = await database.Db.Set<OpenIddictEntityFrameworkCoreToken>().AsNoTracking().ToListAsync();
        var applications = database.Services.GetRequiredService<IOpenIddictApplicationManager>();
        await foreach (var app in applications.ListAsync())
        {
            _ = await applications.GetPermissionsAsync(app);
            _ = await applications.GetRedirectUrisAsync(app);
            _ = await applications.GetPostLogoutRedirectUrisAsync(app);
            _ = await applications.GetRequirementsAsync(app);
            _ = await applications.GetPropertiesAsync(app);
            _ = await applications.GetSettingsAsync(app);
        }
        var scopes = database.Services.GetRequiredService<IOpenIddictScopeManager>();
        await foreach (var scope in scopes.ListAsync())
        {
            _ = await scopes.GetPropertiesAsync(scope);
            _ = await scopes.GetResourcesAsync(scope);
        }
    }

    private static async Task<HttpResponseMessage> Login(HttpClient browser, string email)
    {
        var html = await browser.GetStringAsync("/Account/Login");
        return await browser.PostAsync("/Account/Login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Email"] = email,
            ["Password"] = OidcTestClient.Password,
            ["__RequestVerificationToken"] = OidcTestClient.Field(html, "__RequestVerificationToken")
        }));
    }

    private static string LegacyPasswordHash(string password)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var bytes = new byte[61];
        bytes[0] = 1;
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(1), 2);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(5), 10_000);
        BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(9), 16);
        salt.CopyTo(bytes, 13);
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, 10_000, HashAlgorithmName.SHA512, 32).CopyTo(bytes, 29);
        return Convert.ToBase64String(bytes);
    }
}
