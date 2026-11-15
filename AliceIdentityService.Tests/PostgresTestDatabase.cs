using AliceIdentityService.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Microsoft.Extensions.Configuration;

namespace AliceIdentityService.Tests;

public sealed class PostgresFactAttribute : FactAttribute
{
    public PostgresFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIS3_TEST_ADMIN_CONNECTION")))
            Skip = "Set AIS3_TEST_ADMIN_CONNECTION to run PostgreSQL integration tests against isolated temporary databases.";
    }
}

public sealed class RestoredDatabaseFactAttribute : FactAttribute
{
    public RestoredDatabaseFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AIS3_TEST_RESTORED_CONNECTION")))
            Skip = "Set AIS3_TEST_RESTORED_CONNECTION to an AIS2 dump restored into a separate test database.";
    }
}

[CollectionDefinition("PostgreSQL", DisableParallelization = true)]
public sealed class PostgresCollection;

internal sealed class PostgresTestDatabase : IAsyncDisposable
{
    private readonly string adminConnection;
    private readonly string name;
    private readonly ServiceProvider provider;
    private readonly AsyncServiceScope scope;
    public IServiceProvider Services => scope.ServiceProvider;
    public AisDbContext Db => Services.GetRequiredService<AisDbContext>();

    private PostgresTestDatabase(string adminConnection, string name, string connection)
    {
        this.adminConnection = adminConnection;
        this.name = name;
        provider = ServicesFor(connection).BuildServiceProvider(new ServiceProviderOptions { ValidateScopes = true });
        scope = provider.CreateAsyncScope();
    }

    internal static IServiceCollection ServicesFor(string connection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:DefaultConnection"] = connection
        }).Build();
        return new ServiceCollection().AddLogging().AddSingleton<IConfiguration>(configuration).AddAisData();
    }

    public static async Task<PostgresTestDatabase> CreateAsync()
    {
        var admin = Environment.GetEnvironmentVariable("AIS3_TEST_ADMIN_CONNECTION")!;
        var name = "ais3_test_" + Guid.NewGuid().ToString("N");
        await using var connection = new NpgsqlConnection(admin);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"CREATE DATABASE \"{name}\" TEMPLATE template0", connection);
        await command.ExecuteNonQueryAsync();
        var settings = new NpgsqlConnectionStringBuilder(admin) { Database = name, Pooling = false };
        return new(admin, name, settings.ConnectionString);
    }

    public async Task InitializeSchemaAsync()
    {
        foreach (var name in new[] { "CreateSchema.sql", "PopulateSchema.sql" })
        {
            var sql = SqlResources.Read(name, "Scripts");
            if (!string.IsNullOrWhiteSpace(sql)) await Db.Database.ExecuteSqlRawAsync(sql);
        }
    }

    public async ValueTask DisposeAsync()
    {
        await scope.DisposeAsync();
        await provider.DisposeAsync();
        await using var connection = new NpgsqlConnection(adminConnection);
        await connection.OpenAsync();
        await using var command = new NpgsqlCommand($"DROP DATABASE \"{name}\" WITH (FORCE)", connection);
        await command.ExecuteNonQueryAsync();
    }
}
