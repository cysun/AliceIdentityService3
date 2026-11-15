using AliceIdentityService.Data;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AliceIdentityService.Tests;

public sealed record SchemaEntry(string Kind, string Key, string Definition);
public sealed record SchemaReport(IReadOnlyList<string> Differences)
{
    public bool Compatible => Differences.Count == 0;
}

public sealed class SchemaInspector(AisDbContext db)
{
    private static readonly IReadOnlyList<SchemaEntry> Baseline = JsonSerializer.Deserialize<SchemaEntry[]>(SqlResources.Read("Baseline.json"))!;

    public async Task<IReadOnlyList<SchemaEntry>> ReadCatalogAsync(CancellationToken cancellationToken = default)
    {
        var connection = (NpgsqlConnection)db.Database.GetDbConnection();
        var wasClosed = connection.State != System.Data.ConnectionState.Open;
        if (wasClosed) await connection.OpenAsync(cancellationToken);
        try
        {
            await using var command = new NpgsqlCommand(SqlResources.Read("Catalog.sql"), connection,
                db.Database.CurrentTransaction?.GetDbTransaction() as NpgsqlTransaction);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            var entries = new List<SchemaEntry>();
            while (await reader.ReadAsync(cancellationToken))
                entries.Add(new(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
            return entries;
        }
        finally { if (wasClosed) await connection.CloseAsync(); }
    }

    private static bool IsLegacySearchObject(SchemaEntry entry) =>
        (entry.Kind == "function" && entry.Key == "SearchUsers(character varying, integer)") ||
        (entry.Kind == "index" && entry.Key.StartsWith("AspNetUsers.AspNetUsers_lower_idx", StringComparison.Ordinal));

    public async Task<SchemaReport> CheckAsync(bool includeLegacySearchObjects = true, CancellationToken cancellationToken = default)
    {
        // Compare the unchanged AIS2 contract; Step 8 additive search objects are tested separately.
        bool Include(SchemaEntry entry) => entry.Key != "AspNetUsers.IX_AspNetUsers_Search" &&
            (includeLegacySearchObjects || !IsLegacySearchObject(entry));
        var actual = (await ReadCatalogAsync(cancellationToken)).Where(Include).ToDictionary(entry => (entry.Kind, entry.Key));
        var differences = new List<string>();
        foreach (var expected in Baseline.Where(Include))
        {
            if (!actual.Remove((expected.Kind, expected.Key), out var found))
                differences.Add($"Missing {expected.Kind}: {expected.Key}");
            else if (found.Definition != expected.Definition)
                differences.Add($"Different {expected.Kind}: {expected.Key}");
        }
        differences.AddRange(actual.Values.Select(entry => $"Unexpected {entry.Kind}: {entry.Key}"));
        return new(differences);
    }

}
