using System.Data.Common;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AliceIdentityService.Data;
using AliceIdentityService.Models;
using AliceIdentityService.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using Xunit.Abstractions;

namespace AliceIdentityService.Tests;

[Collection("PostgreSQL")]
public sealed class AdminUserTests(ITestOutputHelper output)
{
    private static async Task<OidcTestClient> Directory()
    {
        var client = await OidcTestClient.Create();
        var now = client.Factory.Clock.GetUtcNow();
        var users = new[]
        {
            new User { Id = "person-a", FirstName = "Maryjohn", LastName = "Smith", ScreenName = "Navigator", Email = "mary@sample.test", EmailConfirmed = true },
            new User { Id = "person-b", FirstName = "John", LastName = "Doe", ScreenName = "Other", Email = "john.doe@sample.test" },
            new User { Id = "person-c", FirstName = "Li", LastName = "王", ScreenName = "", Email = null, EmailConfirmed = true },
            new User { Id = "person-d", FirstName = "José", LastName = "O'Neil", ScreenName = "100%_literal\\name", Email = "literal@sample.test", LockoutEnabled = true, LockoutEnd = now.AddHours(1) },
            new User { Id = "person-e", FirstName = "Expired", LastName = "Lock", ScreenName = "Same name", EmailConfirmed = true, LockoutEnabled = true, LockoutEnd = now.AddHours(-1) },
            new User { Id = "person-f", FirstName = "Disabled", LastName = "Lock", ScreenName = "Same name", EmailConfirmed = true, LockoutEnabled = false, LockoutEnd = now.AddHours(1) }
        };
        foreach (var user in users)
        {
            user.CreationTime = now.UtcDateTime.AddDays(-1);
            user.PasswordHash = "PRIVATE-HASH";
            user.SecurityStamp = "PRIVATE-STAMP";
            user.ConcurrencyStamp = "PRIVATE-CONCURRENCY";
            client.Database.Db.Users.Add(user);
        }
        client.Database.Db.UserClaims.AddRange(
            new IdentityUserClaim<string> { UserId = "person-a", ClaimType = "ais_admin", ClaimValue = "false" },
            new IdentityUserClaim<string> { UserId = "person-a", ClaimType = "ais_admin", ClaimValue = "duplicate" },
            new IdentityUserClaim<string> { UserId = "person-a", ClaimType = "department", ClaimValue = "<script>example</script>" });
        await client.Database.Db.SaveChangesAsync();
        await client.SubmitLogin(await client.Browser.GetStringAsync("/Account/Login"));
        return client;
    }

    private static async Task<AdminUserPage> Page(OidcTestClient client, string query = "")
    {
        var response = await client.Browser.GetAsync("/api/admin/users" + query);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.True(response.Headers.CacheControl!.NoStore);
        return (await response.Content.ReadFromJsonAsync<AdminUserPage>())!;
    }

    [PostgresFact]
    public async Task SearchMatchesSubstringsAllNameFieldsEmailAndTermsInEitherOrder()
    {
        await using var client = await Directory();
        foreach (var query in new[] { "JOHN SMITH", " smith \t john ", "vigat", "mary@sample", "john john smith" })
        {
            var page = await Page(client, "?q=" + Uri.EscapeDataString(query));
            Assert.Equal("person-a", Assert.Single(page.Items).Id);
            Assert.Equal(1, page.TotalCount);
            Assert.True(page.Items[0].Administrator);
        }
        Assert.Equal(2, (await Page(client, "?q=john")).TotalCount);
        Assert.Equal(7, (await Page(client, "?q=%20%20")).TotalCount);
        Assert.Empty((await Page(client, "?q=does-not-exist")).Items);
        Assert.Contains((await Page(client, "?q=Li")).Items, user => user.Id == "person-c");
        Assert.Equal("person-c", Assert.Single((await Page(client, "?q=" + Uri.EscapeDataString("王"))).Items).Id);
        Assert.Equal("person-d", Assert.Single((await Page(client, "?q=" + Uri.EscapeDataString("José"))).Items).Id);
        Assert.Empty((await Page(client, "?q=jose")).Items); // Accent folding is intentionally not enabled.
        Assert.Equal("Li 王", (await Page(client, "?q=Li")).Items.Single(user => user.Id == "person-c").Name);
    }

    [PostgresFact]
    public async Task SearchEscapesSqlPatternsAndDoesNotInterpretInputAsSql()
    {
        await using var client = await Directory();
        foreach (var term in new[] { "%", "_", "\\", "100%_literal\\name", "O'Neil" })
            Assert.Equal("person-d", Assert.Single((await Page(client, "?q=" + Uri.EscapeDataString(term))).Items).Id);
        Assert.Empty((await Page(client, "?q=" + Uri.EscapeDataString("' OR 1=1 --"))).Items);
        Assert.Equal(7, (await Page(client)).TotalCount);
    }

    [PostgresFact]
    public async Task FiltersUseConfirmationAndCurrentEnabledLockoutsWithoutDuplicatingClaimOwners()
    {
        await using var client = await Directory();
        Assert.Equal(5, (await Page(client, "?status=Confirmed")).TotalCount);
        Assert.Equal(2, (await Page(client, "?status=Unconfirmed")).TotalCount);
        var locked = Assert.Single((await Page(client, "?status=Locked")).Items);
        Assert.Equal("person-d", locked.Id);
        Assert.Equal("Locked", locked.Status);
        Assert.Empty((await Page(client, "?q=john&status=Locked")).Items);
        Assert.Single((await Page(client, "?q=john&status=Unconfirmed")).Items);
        client.Factory.Clock.Advance(TimeSpan.FromHours(2));
        Assert.Empty((await Page(client, "?status=Locked")).Items);
        Assert.Equal("Unconfirmed", Assert.Single((await Page(client, "?q=literal")).Items).Status);
    }

    [PostgresFact]
    public async Task PaginationAndAllSortsAreDeterministicAndOutOfRangePagesClampToLastPage()
    {
        await using var client = await Directory();
        var first = await Page(client, "?q=same&sort=name&pageSize=1");
        var second = await Page(client, "?q=same&sort=name&pageSize=1&page=2");
        Assert.Equal("person-e", Assert.Single(first.Items).Id);
        Assert.Equal("person-f", Assert.Single(second.Items).Id);
        Assert.Equal(2, second.TotalCount);
        var last = await Page(client, "?q=same&sort=name&pageSize=1&page=999");
        Assert.Equal(2, last.Page);
        Assert.Equal(second.Items, last.Items);
        foreach (var sort in new[] { "name", "status", "joined" })
        {
            var ascending = await Page(client, $"?sort={sort}&direction=asc");
            var descending = await Page(client, $"?sort={sort}&direction=desc");
            Assert.Equal(7, ascending.Items.Select(user => user.Id).Distinct().Count());
            Assert.Equal(7, descending.Items.Count);
            Assert.Equal("person-e", (await Page(client, $"?q=same&sort={sort}&direction=desc&pageSize=1")).Items[0].Id);
            Assert.NotEqual(ascending.Items[0].Id, descending.Items[0].Id);
        }
        var empty = await Page(client, "?q=not-found&page=100");
        Assert.Equal(1, empty.Page);
        Assert.Equal(25, empty.PageSize);
    }

    [PostgresFact]
    public async Task InvalidQueryBoundsAndSortFieldsReturnBadRequest()
    {
        await using var client = await Directory();
        foreach (var query in new[] { "page=0", "page=10001", "page=oops", "pageSize=0", "pageSize=101", "sort=PasswordHash", "direction=sideways", "status=Administrator", "q=" + new string('x', 257), "q=one+two+three+four+five+six+seven+eight+nine" })
            Assert.Equal(HttpStatusCode.BadRequest, (await client.Browser.GetAsync("/api/admin/users?" + query)).StatusCode);
        Assert.Equal(7, (await Page(client, "?pageSize=100")).TotalCount);
    }

    [PostgresFact]
    public async Task DetailAndListExposeOnlyReviewFieldsAndHandleMissingUsers()
    {
        await using var client = await Directory();
        var response = await client.Browser.GetAsync("/api/admin/users/person-a");
        Assert.True(response.Headers.CacheControl!.NoStore);
        var body = await response.Content.ReadAsStringAsync();
        var detail = JsonSerializer.Deserialize<AdminUserDetail>(body, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal("Maryjohn", detail.FirstName);
        Assert.True(detail.User.Administrator);
        Assert.True(detail.Security.HasPassword);
        Assert.Equal(3, detail.Claims.Count);
        var json = JsonDocument.Parse(body).RootElement;
        Assert.Equal(["claims", "emailConfirmed", "firstName", "lastName", "screenName", "security", "user", "version"], Keys(json));
        Assert.Equal(["accessFailedCount", "hasPassword", "lockoutEnabled", "lockoutEnd", "twoFactorEnabled"], Keys(json.GetProperty("security")));
        Assert.Equal(["type", "value"], Keys(json.GetProperty("claims")[0]));
        var list = await client.Browser.GetFromJsonAsync<JsonElement>("/api/admin/users?q=mary");
        Assert.Equal(["administrator", "createdAt", "email", "id", "name", "status"], Keys(list.GetProperty("items")[0]));
        Assert.DoesNotContain("PRIVATE-", json.ToString());
        Assert.DoesNotContain("PRIVATE-", list.ToString());
        Assert.Equal(HttpStatusCode.NotFound, (await client.Browser.GetAsync("/api/admin/users/missing")).StatusCode);
        Assert.Equal(HttpStatusCode.MethodNotAllowed, (await client.Browser.PostAsJsonAsync("/api/admin/users/person-a", new { firstName = "Changed" })).StatusCode);
        var unchanged = await client.Database.Db.Users.AsNoTracking().SingleAsync(user => user.Id == "person-a");
        Assert.Equal("Maryjohn", unchanged.FirstName);
        Assert.Equal("PRIVATE-HASH", unchanged.PasswordHash);
    }

    private static string[] Keys(JsonElement json) => json.EnumerateObject().Select(property => property.Name).Order().ToArray();

    [PostgresFact]
    public async Task SearchSqlIsRepeatableAndGeneratedQueriesUseTheTrigramIndexOnRepresentativeData()
    {
        await using var database = await PostgresTestDatabase.CreateAsync();
        await database.InitializeSchemaAsync();
        await database.Db.Database.ExecuteSqlRawAsync(SqlResources.Read("PopulateSchema.sql", "Scripts"));
        await database.Db.Database.ExecuteSqlRawAsync("""
            INSERT INTO "AspNetUsers" ("Id", "FirstName", "LastName", "ScreenName", "Email", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled", "LockoutEnabled", "AccessFailedCount")
            SELECT 'fixture-' || i, 'Person' || i, 'Family' || i,
              CASE WHEN i = 12345 THEN 'NeedlePerson' ELSE 'Directory ' || i END,
              'person' || i || '@example.test', i % 10 <> 0, false, false, false, 0
            FROM generate_series(1, 30000) AS i;
            ANALYZE "AspNetUsers";
            """);
        var recorder = new SearchCommandRecorder();
        var options = new DbContextOptionsBuilder<AisDbContext>().UseNpgsql(database.Db.Database.GetConnectionString())
            .UseOpenIddict().AddInterceptors(recorder).Options;
        await using var context = new AisDbContext(options);
        var service = new AdminUserService(context, TimeProvider.System);
        var result = await service.SearchAsync(new() { Q = "needle", Status = "Confirmed" }, default);
        Assert.Equal("fixture-12345", Assert.Single(result.Items).Id);
        await using var connection = new NpgsqlConnection(database.Db.Database.GetConnectionString());
        await connection.OpenAsync();
        Assert.Equal(2, recorder.Commands.Count);
        foreach (var captured in recorder.Commands)
        {
            await using var explain = new NpgsqlCommand("EXPLAIN (ANALYZE, BUFFERS, FORMAT JSON) " + captured.Sql, connection);
            explain.Parameters.AddRange(captured.Parameters);
            var plan = (string)(await explain.ExecuteScalarAsync())!;
            Assert.Contains("IX_AspNetUsers_Search", plan);
            output.WriteLine(plan);
        }
        // Also exercise short/common terms and a deep page without forcing index selection.
        Assert.Equal(30000, (await service.SearchAsync(new() { Q = "pe", Page = 1000 }, default)).TotalCount);
    }

    [RestoredDatabaseFact]
    public async Task UserReadingWorksOnAnAis2RestoreWithoutChangingItsRows()
    {
        using var provider = PostgresTestDatabase.ServicesFor(Environment.GetEnvironmentVariable("AIS3_TEST_RESTORED_CONNECTION")!).BuildServiceProvider();
        await using var scope = provider.CreateAsyncScope();
        var database = scope.ServiceProvider.GetRequiredService<AisDbContext>();
        var service = new AdminUserService(database, TimeProvider.System);
        var page = await service.SearchAsync(new(), default);
        Assert.Equal(await database.Users.LongCountAsync(), page.TotalCount);
        var first = Assert.IsType<AdminUserSummary>(page.Items.First());
        Assert.NotNull(await service.FindAsync(first.Id, default));
        if (!string.IsNullOrWhiteSpace(first.Email))
            Assert.Contains((await service.SearchAsync(new() { Q = first.Email, PageSize = 100 }, default)).Items, user => user.Id == first.Id);
    }

    private sealed class SearchCommandRecorder : DbCommandInterceptor
    {
        public List<(string Sql, NpgsqlParameter[] Parameters)> Commands { get; } = [];
        public override ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(DbCommand command,
            CommandEventData eventData, InterceptionResult<DbDataReader> result, CancellationToken cancellationToken = default)
        {
            Commands.Add((command.CommandText, command.Parameters.Cast<NpgsqlParameter>().Select(parameter => parameter.Clone()).ToArray()));
            return ValueTask.FromResult(result);
        }
    }
}
