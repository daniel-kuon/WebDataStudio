using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// `?u=` opened for real: what the switch allows, what it refuses, and the sentence that says why.
public class UrlConnectionEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-url-conns").FullName;
    private readonly string _files;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public UrlConnectionEndpointTests()
    {
        _files = Directory.CreateDirectory(Path.Combine(_dir, "mounted")).FullName;

        Sqlite(Path.Combine(_files, "shop.sqlite3"));
        File.WriteAllText(Path.Combine(_files, "people.csv"), "id,name\n1,ada\n");
    }

    /// A real database rather than a file that merely starts like one: the tests below list it and
    /// read its schema.
    private static void Sqlite(string path)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}");
        db.Open();
        using var command = db.CreateCommand();
        command.CommandText = "CREATE TABLE orders (id INTEGER PRIMARY KEY, total REAL);";
        command.ExecuteNonQuery();
    }

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var config = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db"),
            ["WDS_FILE_ROOTS"] = _files,
        };

        foreach (var (key, value) in settings) config[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));
    }

    private static async Task<JsonDocument> OpenAsync(HttpClient client, string u)
    {
        var response = await client.PostAsJsonAsync("/api/connections/from-url", new { u }, Ct);
        response.EnsureSuccessStatusCode();
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Off_by_default_nothing_opens()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var answer = await OpenAsync(client, Path.Combine(_files, "shop.sqlite3"));
        var only = answer.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Contains("WDS_OPEN_FROM_URL", only.GetProperty("refused").GetString());

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.Equal(0, list.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task A_file_inside_a_root_opens_read_only_and_is_visible_to_this_client()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        using var answer = await OpenAsync(client, $"sales:{Path.Combine(_files, "shop.sqlite3")}");
        var only = answer.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Null(only.GetProperty("refused").GetString());
        Assert.Equal("sales", only.GetProperty("label").GetString());

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        var connection = list.RootElement.EnumerateArray().Single();

        Assert.Equal("sales", connection.GetProperty("name").GetString());
        Assert.Equal("sqlite", connection.GetProperty("engine").GetString());
        Assert.True(connection.GetProperty("readOnly").GetBoolean());
    }

    /// It is a connection like any other, which is the whole point: the tree opens.
    [Fact]
    public async Task The_studio_can_read_what_the_link_opened()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        using var answer = await OpenAsync(client, Path.Combine(_files, "shop.sqlite3"));
        var id = answer.RootElement.GetProperty("opened").EnumerateArray().Single()
            .GetProperty("id").GetString();

        using var tree = JsonDocument.Parse(await client.GetStringAsync($"/api/schema/{id}", Ct));

        Assert.NotEmpty(tree.RootElement.EnumerateArray());
    }

    [Fact]
    public async Task A_file_outside_the_roots_is_refused_with_the_setting_that_would_allow_it()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        var outside = Path.Combine(_dir, "elsewhere.sqlite3");
        File.WriteAllText(outside, "SQLite format 3\0");

        using var answer = await OpenAsync(client, outside);
        var only = answer.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Contains("WDS_FILE_ROOTS", only.GetProperty("refused").GetString());
    }

    /// A `.db` is whatever somebody renamed, and the answer says so rather than handing the driver a
    /// text file.
    [Fact]
    public async Task A_file_that_is_not_a_database_is_refused()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        var pretender = Path.Combine(_files, "notes.db");
        File.WriteAllText(pretender, "these are notes, not a database");

        using var answer = await OpenAsync(client, pretender);
        var only = answer.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Contains("SQLite format 3", only.GetProperty("refused").GetString());
    }

    [Fact]
    public async Task A_connection_string_needs_its_own_word_in_the_switch()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "true"));
        using var client = factory.CreateClient();

        using var refused = await OpenAsync(client, "Host=db;Database=shop;Username=u;Password=p");
        Assert.Contains("connection-string", refused.RootElement
            .GetProperty("opened").EnumerateArray().Single().GetProperty("refused").GetString());

        using var allowing = Factory(("WDS_OPEN_FROM_URL", "connection-string"));
        using var second = allowing.CreateClient();

        using var opened = await OpenAsync(second, "shop:Data Source=:memory:");
        Assert.Null(opened.RootElement.GetProperty("opened").EnumerateArray()
            .Single().GetProperty("refused").GetString());
    }

    /// Nothing guesses an engine out of thin air, and the refusal shows how to say it.
    [Fact]
    public async Task A_connection_string_that_names_no_engine_is_refused()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "connection-string"));
        using var client = factory.CreateClient();

        using var answer = await OpenAsync(client, "Foo=1;Bar=2");
        var only = answer.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Contains("which engine", only.GetProperty("refused").GetString());
        // And never the string itself: it is a password in a log line.
        Assert.DoesNotContain("Foo=1", only.GetProperty("label").GetString());
    }

    [Fact]
    public async Task One_refused_entry_does_not_take_the_others_with_it()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        using var answer = await OpenAsync(client,
            $"{Path.Combine(_files, "shop.sqlite3")},{Path.Combine(_dir, "nope.sqlite3")}");

        var opened = answer.RootElement.GetProperty("opened").EnumerateArray().ToList();

        Assert.Equal(2, opened.Count);
        Assert.Null(opened[0].GetProperty("refused").GetString());
        Assert.NotNull(opened[1].GetProperty("refused").GetString());
    }

    [Fact]
    public async Task Opening_the_same_link_twice_is_one_connection()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        var u = Path.Combine(_files, "shop.sqlite3");
        using var _ = await OpenAsync(client, u);
        using var __ = await OpenAsync(client, u);

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.Equal(1, list.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Writing_is_allowed_only_where_the_deployment_says_so()
    {
        using var writable = Factory(("WDS_OPEN_FROM_URL", "file"), ("WDS_OPEN_FROM_URL_WRITABLE", "true"));
        using var client = writable.CreateClient();

        using var _ = await OpenAsync(client, Path.Combine(_files, "shop.sqlite3"));

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.False(list.RootElement.EnumerateArray().Single().GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public async Task A_data_file_stays_read_only_even_when_writing_is_allowed()
    {
        using var writable = Factory(("WDS_OPEN_FROM_URL", "file"), ("WDS_OPEN_FROM_URL_WRITABLE", "true"));
        using var client = writable.CreateClient();

        using var _ = await OpenAsync(client, Path.Combine(_files, "people.csv"));

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        var only = list.RootElement.EnumerateArray().Single();

        Assert.Equal("storage", only.GetProperty("engine").GetString());
        Assert.True(only.GetProperty("readOnly").GetBoolean());
    }

    [Fact]
    public async Task Keeping_them_in_the_store_is_the_other_branch()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"), ("WDS_OPEN_FROM_URL_KEEP", "store"));
        using var first = factory.CreateClient();
        using var second = factory.CreateClient();

        using var _ = await OpenAsync(first, Path.Combine(_files, "shop.sqlite3"));

        // Stored means everybody sees it, which is exactly what session does not do.
        using var list = JsonDocument.Parse(await second.GetStringAsync("/api/connections", Ct));
        Assert.Equal(1, list.RootElement.GetArrayLength());
    }

    /// Nothing at all in `?u=` is not an error — the studio just opens normally.
    [Fact]
    public async Task Nothing_in_the_parameter_opens_nothing()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        using var answer = await OpenAsync(client, "  ");

        Assert.Empty(answer.RootElement.GetProperty("opened").EnumerateArray());
    }
}
