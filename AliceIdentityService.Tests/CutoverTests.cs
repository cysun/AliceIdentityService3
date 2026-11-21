using System.Text.Json;
using System.Text.Json.Serialization;
using AliceIdentityService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace AliceIdentityService.Tests;

public sealed class CutoverDatabaseFactAttribute : FactAttribute
{
    public CutoverDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIS3_CUTOVER_SOURCE_CONNECTION")) &&
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIS3_CUTOVER_TARGET_CONNECTION")))
            Skip = "Set AIS3_CUTOVER_SOURCE_CONNECTION and AIS3_CUTOVER_TARGET_CONNECTION for read-only final-restore validation.";
    }
}

[Collection("PostgreSQL")]
public sealed class CutoverTests
{
    private sealed record CutoverReport(
        [property: JsonPropertyName("recordedUtc")] DateTimeOffset RecordedUtc,
        [property: JsonPropertyName("passed")] bool Passed,
        [property: JsonPropertyName("completedChecks")] List<string> CompletedChecks,
        [property: JsonPropertyName("fingerprint")] DatabaseFingerprint? Fingerprint,
        [property: JsonPropertyName("limitations")] string Limitations);

    [CutoverDatabaseFact]
    public async Task FinalRestoreMatchesFrozenAis2BeforeApplicationWrites()
    {
        var source = Environment.GetEnvironmentVariable("AIS3_CUTOVER_SOURCE_CONNECTION");
        var target = Environment.GetEnvironmentVariable("AIS3_CUTOVER_TARGET_CONNECTION");
        Assert.False(string.IsNullOrWhiteSpace(source), "Configure the source connection privately.");
        Assert.False(string.IsNullOrWhiteSpace(target), "Configure the target connection privately.");
        // Require distinct database names even on different servers, preventing a comparison to itself
        // through host aliases. This cutover workflow restores ais2 into a separately named AIS3 database.
        Assert.False(string.Equals(new NpgsqlConnectionStringBuilder(source).Database,
            new NpgsqlConnectionStringBuilder(target).Database, StringComparison.Ordinal), "Source and target database names must differ.");
        var output = Environment.GetEnvironmentVariable("AIS3_CUTOVER_REPORT_DIRECTORY");
        Assert.False(string.IsNullOrWhiteSpace(output), "Set AIS3_CUTOVER_REPORT_DIRECTORY to private evidence storage.");
        var checks = new List<string>();
        DatabaseFingerprint? baseline = null;
        var passed = false;
        try
        {
            baseline = await MigrationRehearsal.FingerprintConnectionAsync(source!);
            var restored = await MigrationRehearsal.FingerprintConnectionAsync(target!);
            MigrationRehearsal.RequireEqual(baseline, restored);
            checks.Add("All public table counts/content fingerprints, sequence state and shared catalog match.");
            await using (var provider = PostgresTestDatabase.ServicesFor(target!).BuildServiceProvider())
            await using (var scope = provider.CreateAsyncScope())
            {
                var db = scope.ServiceProvider.GetRequiredService<AisDbContext>();
                await using var transaction = await db.Database.BeginTransactionAsync(System.Data.IsolationLevel.RepeatableRead);
                await db.Database.ExecuteSqlRawAsync("SET TRANSACTION READ ONLY");
                var inspector = new SchemaInspector(db);
                Assert.True((await inspector.CheckAsync(includeLegacySearchObjects: false)).Compatible, "Target schema differs from the AIS3 model baseline.");
                var index = await SearchIndexMatchesAsync(db);
                Assert.True(index, "Apply the reviewed PopulateSchema.sql to the target before validation.");
                _ = await db.Users.AsNoTracking().CountAsync();
            }
            checks.Add("AIS3 shared schema, valid search index and user-store read verified without writes.");
            MigrationRehearsal.RequireEqual(baseline, await MigrationRehearsal.FingerprintConnectionAsync(source!));
            MigrationRehearsal.RequireEqual(restored, await MigrationRehearsal.FingerprintConnectionAsync(target!));
            checks.Add("Fresh read-only comparisons found no source/target changes during validation.");
            passed = true;
        }
        finally
        {
            Directory.CreateDirectory(output!);
            var path = Path.Combine(output!, $"cutover-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            await using var file = new FileStream(path, options);
            var report = new CutoverReport(
                DateTimeOffset.UtcNow,
                passed,
                checks,
                baseline,
                "Read-only data validation before application writes; not a deployment or client smoke-test approval.");
            await JsonSerializer.SerializeAsync(file, report, new JsonSerializerOptions { WriteIndented = true });
        }
    }

    [PostgresFact]
    public async Task FinalValidationRejectsADifferentIndexWithTheApprovedName()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        Assert.True(await SearchIndexMatchesAsync(database.Db));
        await database.Db.Database.ExecuteSqlRawAsync("""
            DROP INDEX "IX_AspNetUsers_Search";
            CREATE INDEX "IX_AspNetUsers_Search" ON "AspNetUsers" ("Email");
            """);
        Assert.False(await SearchIndexMatchesAsync(database.Db));
    }

    private static Task<bool> SearchIndexMatchesAsync(AisDbContext db)
    {
        // PostgreSQL's canonical definition of the reviewed PopulateSchema expression. A change
        // in PostgreSQL's deparser requires review, not silently accepting a different index.
        const string expected = """
            CREATE INDEX "IX_AspNetUsers_Search" ON public."AspNetUsers" USING gin ((((((((("FirstName")::text || ' '::text) || ("LastName")::text) || ' '::text) || ("ScreenName")::text) || ' '::text) || (COALESCE("Email", ''::character varying))::text)) gin_trgm_ops)
            """;
        return db.Database.SqlQuery<bool>($"""
            SELECT EXISTS (
                SELECT 1 FROM pg_index i JOIN pg_class c ON c.oid = i.indexrelid
                JOIN pg_namespace n ON n.oid = c.relnamespace
                WHERE n.nspname = 'public' AND c.relname = 'IX_AspNetUsers_Search'
                  AND i.indisvalid AND i.indisready AND pg_get_indexdef(i.indexrelid) = {expected}
            ) AS "Value"
            """).SingleAsync();
    }
}
