using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests.Editing;

/// The query bar's browse: several filters, several sort columns, joins along the table's own
/// foreign keys, grouping with aggregates — and the reverse direction, "who references this table".
public class BrowseQueryEndpointTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-browse").FullName;
    private string _db = "";

    public async ValueTask InitializeAsync()
    {
        _db = Path.Combine(_dir, "shop.db");
        await using var db = new SqliteConnection($"Data Source={_db}");
        await db.OpenAsync();
        await using var cmd = db.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE customers (
                id INTEGER PRIMARY KEY, name TEXT NOT NULL, api_key TEXT);
            CREATE TABLE orders (
                id INTEGER PRIMARY KEY,
                customer_id INTEGER NOT NULL REFERENCES customers(id),
                amount REAL NOT NULL,
                state TEXT NOT NULL);
            INSERT INTO customers VALUES (1,'ada','k1'),(2,'linus','k2'),(3,'grace','k3');
            INSERT INTO orders VALUES
                (1,1,10.0,'open'),(2,1,20.0,'paid'),(3,2,5.0,'open'),(4,2,15.0,'open'),(5,2,30.0,'paid');
            """;
        await cmd.ExecuteNonQueryAsync();
    }

    public ValueTask DisposeAsync()
    {
        TestDirectory.Remove(_dir);
        return ValueTask.CompletedTask;
    }

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
                ["WDS_CONN_SHOP"] = $"sqlite:///{_db.Replace('\\', '/')}",
            })));

    private static async Task<string> ConnectionIdAsync(HttpClient client)
    {
        using var document = JsonDocument.Parse(
            await client.GetStringAsync("/api/connections", TestContext.Current.CancellationToken));
        return document.RootElement[0].GetProperty("id").GetString()!;
    }

    private const string OrdersRef = "Table%3Amain%2Forders";

    private static async Task<JsonElement> BrowseAsync(HttpClient client, string conn, object body)
    {
        var response = await client.PostAsJsonAsync($"/api/data/{conn}/browse?ref={OrdersRef}", body,
            TestContext.Current.CancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<JsonElement>(TestContext.Current.CancellationToken);
    }

    private static string[] ColumnNames(JsonElement page) =>
        [.. page.GetProperty("columns").EnumerateArray().Select(c => c.GetProperty("name").GetString()!)];

    [Fact]
    public async Task Several_filters_apply_together()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var page = await BrowseAsync(client, conn, new
        {
            filters = new object[]
            {
                new { column = "state", op = "eq", value = "open" },
                new { column = "amount", op = "gte", value = "10" },
            },
        });

        // Of the three open orders, one is below 10.
        Assert.Equal(2, page.GetProperty("rows").GetArrayLength());
        Assert.True(page.GetProperty("editable").GetBoolean());
    }

    [Fact]
    public async Task Several_sort_columns_order_by_the_first_then_the_second()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var page = await BrowseAsync(client, conn, new
        {
            sort = new object[]
            {
                new { column = "state", desc = false },
                new { column = "amount", desc = true },
            },
        });

        var rows = page.GetProperty("rows");
        // open before paid, and within open the amounts fall: 20 would be paid, so 15 leads.
        Assert.Equal("open", rows[0][3].GetString());
        Assert.Equal(15.0, rows[0][2].GetDouble());
        Assert.Equal(10.0, rows[1][2].GetDouble());
    }

    [Fact]
    public async Task A_join_brings_the_referenced_columns_prefixed_and_read_only()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var page = await BrowseAsync(client, conn, new { joins = new[] { "fk_orders_0" } });

        var names = ColumnNames(page);
        Assert.Contains("customers.name", names);
        Assert.Equal(5, page.GetProperty("rows").GetArrayLength());
        Assert.False(page.GetProperty("editable").GetBoolean());
        Assert.Contains("joined", page.GetProperty("reason").GetString());

        // The rows carry the joined values, not just the columns.
        var nameIndex = Array.IndexOf(names, "customers.name");
        var customerIndex = Array.IndexOf(names, "customer_id");
        foreach (var row in page.GetProperty("rows").EnumerateArray())
            Assert.Equal(row[customerIndex].GetInt32() == 1 ? "ada" : "linus",
                row[nameIndex].GetString());
    }

    [Fact]
    public async Task A_masked_column_stays_masked_behind_a_join()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var page = await BrowseAsync(client, conn, new { joins = new[] { "fk_orders_0" } });

        var names = ColumnNames(page);
        var masked = Array.IndexOf(names, "customers.api_key");
        Assert.True(masked >= 0);
        Assert.True(page.GetProperty("columns")[masked].GetProperty("masked").GetBoolean());
        Assert.Equal("••••••••", page.GetProperty("rows")[0][masked].GetString());
    }

    [Fact]
    public async Task Grouping_answers_the_group_columns_and_the_aggregates()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var page = await BrowseAsync(client, conn, new
        {
            groupBy = new[] { "state" },
            aggregates = new object[]
            {
                new { function = "count", column = (string?)null },
                new { function = "sum", column = "amount" },
            },
            sort = new object[] { new { column = "state", desc = false } },
        });

        Assert.Equal(new[] { "state", "count(*)", "sum(amount)" }, ColumnNames(page));
        Assert.True(page.GetProperty("grouped").GetBoolean());
        Assert.False(page.GetProperty("editable").GetBoolean());

        var rows = page.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("open", rows[0][0].GetString());
        Assert.Equal(3, rows[0][1].GetInt32());
        Assert.Equal(30.0, rows[0][2].GetDouble());
    }

    [Fact]
    public async Task Grouping_by_a_joined_column_works()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var page = await BrowseAsync(client, conn, new
        {
            joins = new[] { "fk_orders_0" },
            groupBy = new[] { "customers.name" },
            aggregates = new object[] { new { function = "sum", column = "amount" } },
            sort = new object[] { new { column = "sum(amount)", desc = true } },
        });

        var rows = page.GetProperty("rows");
        Assert.Equal(2, rows.GetArrayLength());
        Assert.Equal("linus", rows[0][0].GetString());
        Assert.Equal(50.0, rows[0][1].GetDouble());
    }

    [Fact]
    public async Task An_unknown_column_or_join_is_a_bad_request_not_an_engine_error()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);
        var ct = TestContext.Current.CancellationToken;

        var filter = await client.PostAsJsonAsync($"/api/data/{conn}/browse?ref={OrdersRef}",
            new { filters = new object[] { new { column = "nope", op = "eq", value = "1" } } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, filter.StatusCode);

        var join = await client.PostAsJsonAsync($"/api/data/{conn}/browse?ref={OrdersRef}",
            new { joins = new[] { "fk_made_up" } }, ct);
        Assert.Equal(HttpStatusCode.BadRequest, join.StatusCode);
        Assert.Contains("fk_orders_0",
            (await join.Content.ReadFromJsonAsync<JsonElement>(ct)).GetProperty("message").GetString());
    }

    [Fact]
    public async Task A_filter_value_with_a_quote_is_data_not_sql()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);

        var page = await BrowseAsync(client, conn, new
        {
            filters = new object[] { new { column = "state", op = "eq", value = "open' OR '1'='1" } },
        });

        Assert.Equal(0, page.GetProperty("rows").GetArrayLength());
    }

    [Fact]
    public async Task Referencing_answers_who_points_at_a_table()
    {
        using var factory = Factory();
        var client = factory.CreateClient();
        var conn = await ConnectionIdAsync(client);
        var ct = TestContext.Current.CancellationToken;

        var keys = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{conn}/referencing?ref=Table%3Amain%2Fcustomers", ct);

        var entry = Assert.Single(keys.EnumerateArray());
        Assert.Equal("orders", entry.GetProperty("table").GetString());
        Assert.Equal("Table:main/orders", entry.GetProperty("tableRef").GetString());
        Assert.Equal("customer_id", entry.GetProperty("columns")[0].GetString());
        Assert.Equal("id", entry.GetProperty("referencedColumns")[0].GetString());

        // Nothing points at orders.
        var none = await client.GetFromJsonAsync<JsonElement>(
            $"/api/schema/{conn}/referencing?ref={OrdersRef}", ct);
        Assert.Equal(0, none.GetArrayLength());
    }
}
