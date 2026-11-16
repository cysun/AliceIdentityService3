using System.Buffers.Binary;
using System.Data;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace AliceIdentityService.Tests;

public sealed class MigrationSourceFactAttribute : FactAttribute
{
    public MigrationSourceFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIS3_TEST_MIGRATION_SOURCE_CONNECTION")) ||
            string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIS3_TEST_ADMIN_CONNECTION")))
            Skip = "Set AIS3_TEST_MIGRATION_SOURCE_CONNECTION and AIS3_TEST_ADMIN_CONNECTION for the read-only AIS2 dump rehearsal.";
    }
}

internal sealed record TableFingerprint(string Table, long Rows, string Sha256);
internal sealed record SequenceFingerprint(string Sequence, long LastValue, bool IsCalled);
internal sealed record DatabaseFingerprint(TableFingerprint[] Tables, SequenceFingerprint[] Sequences, string SchemaSha256);

// Test tooling only: never registered in the application or included in deployment.
// Targets are always new databases owned by PostgresTestDatabase, never caller-supplied names.
internal sealed class MigrationRehearsal : IAsyncDisposable
{
    private readonly string directory = Path.Combine(Path.GetTempPath(), "ais3-migration-" + Guid.NewGuid().ToString("N"));
    private string DumpPath => Path.Combine(directory, "source.dump");
    public DatabaseFingerprint Source { get; private set; } = null!;
    public Dictionary<string, long> ReviewCounts { get; private set; } = [];
    public string DumpSha256 { get; private set; } = "";

    public MigrationRehearsal()
    {
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(directory);
        else Directory.CreateDirectory(directory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }

    public async Task CaptureAsync(string sourceConnection)
    {
        // Export one read-only snapshot for both the fingerprint and pg_dump. No source DDL/DML.
        var settings = new NpgsqlConnectionStringBuilder(sourceConnection) { Pooling = false, IncludeErrorDetail = false, LogParameters = false };
        await using var connection = new NpgsqlConnection(settings.ConnectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await Execute(connection, "SET TRANSACTION READ ONLY; SET LOCAL TIME ZONE 'UTC'");
        var snapshot = (string)(await new NpgsqlCommand("SELECT pg_export_snapshot()", connection).ExecuteScalarAsync())!;
        Source = await FingerprintAsync(connection);
        ReviewCounts = await ReadReviewCounts(connection);
        await using (var file = PrivateFile(DumpPath)) { }
        await PostgresTool("pg_dump", settings, readOnly: true, "--format=custom", "--no-owner", "--no-privileges",
            "--snapshot=" + snapshot, "--file=" + DumpPath);
        // Sequences are not MVCC snapshot data. Refuse a moving source rather than claim equality.
        RequireEqual(Source, await FingerprintAsync(connection));
        await transaction.CommitAsync();
        await using var dump = File.OpenRead(DumpPath);
        DumpSha256 = Convert.ToHexString(await SHA256.HashDataAsync(dump));
    }

    public async Task RestoreAsync(PostgresTestDatabase target)
    {
        // Refuse even a partially initialized test target; never use pg_restore --clean/--create.
        if ((await new SchemaInspector(target.Db).ReadCatalogAsync()).Count != 0)
            throw new InvalidOperationException("The rehearsal target must be empty.");
        await PostgresTool("pg_restore", new(target.Db.Database.GetConnectionString()), readOnly: false,
            "--no-owner", "--no-privileges", "--exit-on-error", "--single-transaction", "--dbname=" + target.Db.Database.GetDbConnection().Database, DumpPath);
        RequireEqual(Source, await FingerprintAsync(target));
    }

    public static async Task<DatabaseFingerprint> FingerprintAsync(PostgresTestDatabase database)
        => await FingerprintConnectionAsync(database.Db.Database.GetConnectionString()!);

    public static async Task<DatabaseFingerprint> FingerprintConnectionAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var transaction = await connection.BeginTransactionAsync(IsolationLevel.RepeatableRead);
        await Execute(connection, "SET TRANSACTION READ ONLY; SET LOCAL TIME ZONE 'UTC'");
        return await FingerprintAsync(connection);
    }

    private static async Task<DatabaseFingerprint> FingerprintAsync(NpgsqlConnection connection)
    {
        var tables = new List<TableFingerprint>();
        var names = await Names(connection, "SELECT tablename FROM pg_tables WHERE schemaname = 'public' ORDER BY tablename COLLATE \"C\"");
        foreach (var table in names)
        {
            // Canonical JSON includes every column, including password/secret hashes, JSON strings,
            // stamps and token payloads. Only the aggregate digest leaves this method.
            await using var command = new NpgsqlCommand($"SELECT to_jsonb(t)::text FROM public.{Quote(table)} t ORDER BY to_jsonb(t)::text COLLATE \"C\"", connection);
            command.CommandTimeout = 300;
            await using var reader = await command.ExecuteReaderAsync(CommandBehavior.SequentialAccess);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long count = 0;
            var length = new byte[8];
            while (await reader.ReadAsync())
            {
                var row = Encoding.UTF8.GetBytes(reader.GetString(0));
                BinaryPrimitives.WriteInt64BigEndian(length, row.LongLength);
                hash.AppendData(length);
                hash.AppendData(row);
                count++;
            }
            tables.Add(new(table, count, Convert.ToHexString(hash.GetHashAndReset())));
        }
        var sequences = new List<SequenceFingerprint>();
        foreach (var name in await Names(connection, "SELECT sequencename FROM pg_sequences WHERE schemaname = 'public' ORDER BY sequencename COLLATE \"C\""))
        {
            await using var command = new NpgsqlCommand($"SELECT last_value, is_called FROM public.{Quote(name)}", connection);
            await using var reader = await command.ExecuteReaderAsync();
            await reader.ReadAsync();
            sequences.Add(new(name, reader.GetInt64(0), reader.GetBoolean(1)));
        }
        // Compare the whole public catalog, retaining legacy search objects and history.
        // PopulateSchema's one additive index is checked separately by the search suite.
        using var schemaHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var command = new NpgsqlCommand(SqlResources.Read("Catalog.sql"), connection))
        await using (var reader = await command.ExecuteReaderAsync())
            while (await reader.ReadAsync())
            {
                if (reader.GetString(0) == "index" && reader.GetString(1) == "AspNetUsers.IX_AspNetUsers_Search") continue;
                schemaHash.AppendData(JsonSerializer.SerializeToUtf8Bytes(new[] { reader.GetString(0), reader.GetString(1), reader.GetString(2) }));
            }
        return new(tables.ToArray(), sequences.ToArray(), Convert.ToHexString(schemaHash.GetHashAndReset()));
    }

    public static void RequireEqual(DatabaseFingerprint expected, DatabaseFingerprint actual)
    {
        if (!expected.Tables.SequenceEqual(actual.Tables) || !expected.Sequences.SequenceEqual(actual.Sequences) || expected.SchemaSha256 != actual.SchemaSha256)
            throw new InvalidOperationException("Migration validation failed: table contents/counts, sequence state or shared schema differ. No row values are logged.");
    }

    private static async Task<Dictionary<string, long>> ReadReviewCounts(NpgsqlConnection connection)
    {
        // Counts are review prompts, not automatic repairs or a claim that real clients were tested.
        var queries = new Dictionary<string, string>
        {
            ["unconfirmedUsers"] = "SELECT count(*) FROM \"AspNetUsers\" WHERE NOT \"EmailConfirmed\"",
            ["twoFactorUsersBlockedByAis3"] = "SELECT count(*) FROM \"AspNetUsers\" WHERE \"TwoFactorEnabled\"",
            ["usersWithoutPasswords"] = "SELECT count(*) FROM \"AspNetUsers\" WHERE \"PasswordHash\" IS NULL",
            ["usersWithoutSecurityStamps"] = "SELECT count(*) FROM \"AspNetUsers\" WHERE COALESCE(\"SecurityStamp\", '') = ''",
            ["duplicateNormalizedEmailGroups"] = "SELECT count(*) FROM (SELECT \"NormalizedEmail\" FROM \"AspNetUsers\" WHERE \"NormalizedEmail\" IS NOT NULL GROUP BY \"NormalizedEmail\" HAVING count(*) > 1) d",
            ["administrators"] = "SELECT count(DISTINCT \"UserId\") FROM \"AspNetUserClaims\" WHERE \"ClaimType\" = 'ais_admin'",
            ["clientsOutsideCodeOrImplicitConsentProfile"] = "SELECT count(*) FROM \"OpenIddictApplications\" WHERE \"ClientType\" IS NULL OR \"ClientType\" NOT IN ('public', 'confidential') OR \"ConsentType\" IS DISTINCT FROM 'implicit' OR NOT COALESCE(\"Permissions\"::jsonb ? 'gt:authorization_code', false) OR NOT COALESCE(\"Permissions\"::jsonb ? 'rst:code', false)",
            ["publicClientsWithoutPkceRequirement"] = "SELECT count(*) FROM \"OpenIddictApplications\" WHERE \"ClientType\" = 'public' AND NOT COALESCE(\"Requirements\"::jsonb ? 'ft:pkce', false)",
            ["confidentialClientsWithoutSecrets"] = "SELECT count(*) FROM \"OpenIddictApplications\" WHERE \"ClientType\" = 'confidential' AND COALESCE(\"ClientSecret\", '') = ''",
            ["validAuthorizationsRetained"] = "SELECT count(*) FROM \"OpenIddictAuthorizations\" WHERE \"Status\" = 'valid'",
            ["validTokenRowsRetainedNotContinuityEvidence"] = "SELECT count(*) FROM \"OpenIddictTokens\" WHERE \"Status\" = 'valid'"
        };
        var counts = new Dictionary<string, long>();
        foreach (var (name, sql) in queries)
            counts[name] = (long)(await new NpgsqlCommand(sql, connection).ExecuteScalarAsync())!;
        return counts;
    }

    public async Task WriteReportAsync(string label, IReadOnlyList<string> completedChecks, bool passed)
    {
        var output = Environment.GetEnvironmentVariable("AIS3_TEST_MIGRATION_REPORT_DIRECTORY");
        if (string.IsNullOrWhiteSpace(output)) return;
        Directory.CreateDirectory(output);
        var path = Path.Combine(output, $"{label}-{DateTime.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}.json");
        await using var file = PrivateFile(path);
        await JsonSerializer.SerializeAsync(file, new
        {
            formatVersion = 1,
            recordedUtc = DateTimeOffset.UtcNow,
            passed,
            completedChecks,
            dumpSha256 = DumpSha256,
            fingerprint = Source,
            reviewCounts = ReviewCounts,
            limitations = new[] { "Local rehearsal, not production approval.", "No real-user passwords or deployed-client credentials exercised.",
                "Sequences require a quiescent source; concurrent source changes cause validation failure.",
                "No mailbox delivery, Linux proxy or production key-continuity claim." }
        }, new JsonSerializerOptions { WriteIndented = true });
    }

    private static FileStream PrivateFile(string path)
    {
        var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write };
        if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
        return new FileStream(path, options);
    }

    private static async Task PostgresTool(string tool, NpgsqlConnectionStringBuilder settings, bool readOnly, params string[] arguments)
    {
        var start = new ProcessStartInfo(tool) { RedirectStandardError = true, RedirectStandardOutput = true, UseShellExecute = false };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("PG", StringComparison.Ordinal)).ToArray()) start.Environment.Remove(key);
        start.Environment["PGHOST"] = settings.Host;
        start.Environment["PGPORT"] = settings.Port.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["PGDATABASE"] = settings.Database;
        start.Environment["PGUSER"] = settings.Username;
        if (settings.Password is not null) start.Environment["PGPASSWORD"] = settings.Password;
        start.Environment["PGSSLMODE"] = settings.SslMode.ToString().ToLowerInvariant().Replace("verifyca", "verify-ca").Replace("verifyfull", "verify-full");
        if (settings.RootCertificate is not null) start.Environment["PGSSLROOTCERT"] = settings.RootCertificate;
        start.Environment["PGCONNECT_TIMEOUT"] = "10";
        start.Environment["PGOPTIONS"] = (settings.Options ?? "") + (readOnly ? " -c default_transaction_read_only=on" : "");
        start.ArgumentList.Add("--no-password");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start {tool}.");
        var errors = process.StandardError.ReadToEndAsync();
        var output = process.StandardOutput.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(5));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); throw; }
        await output;
        // stderr may contain account data or connection details. Do not copy it into test output.
        if (process.ExitCode != 0 || !string.IsNullOrWhiteSpace(await errors))
            throw new InvalidOperationException($"{tool} failed or emitted warnings (exit {process.ExitCode}); details withheld from test logs.");
    }

    private static string Quote(string identifier) => '"' + identifier.Replace("\"", "\"\"") + '"';
    private static async Task Execute(NpgsqlConnection connection, string sql) => await new NpgsqlCommand(sql, connection).ExecuteNonQueryAsync();
    private static async Task<string[]> Names(NpgsqlConnection connection, string sql)
    {
        await using var command = new NpgsqlCommand(sql, connection);
        await using var reader = await command.ExecuteReaderAsync();
        var result = new List<string>();
        while (await reader.ReadAsync()) result.Add(reader.GetString(0));
        return result.ToArray();
    }

    public ValueTask DisposeAsync()
    {
        Directory.Delete(directory, recursive: true);
        return ValueTask.CompletedTask;
    }
}
