using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// The studio anybody brings their own data to: what a visitor adds is theirs, the next visitor sees
/// none of it, and nothing about it is written down.
public class SessionScopeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-session-scope").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var config = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db"),
            ["WDS_CONNECTION_SCOPE"] = "session",
        };

        foreach (var (key, value) in settings) config[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));
    }

    private static object Connection(string name, string? connectionString = null) => new
    {
        name,
        engine = "sqlite",
        connectionString = connectionString ?? "Data Source=:memory:",
        readOnly = true,
    };

    private static async Task<JsonElement> AddAsync(HttpClient client, string name,
        string? connectionString = null)
    {
        var answer = await client.PostAsJsonAsync("/api/connections",
            Connection(name, connectionString), Ct);

        answer.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(Ct));
        return body.RootElement.Clone();
    }

    private static async Task<List<JsonElement>> ListAsync(HttpClient client)
    {
        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        return list.RootElement.EnumerateArray().Select(e => e.Clone()).ToList();
    }

    [Fact]
    public async Task What_the_form_makes_is_a_session_connection()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var made = await AddAsync(client, "SHOP");

        Assert.Equal("Session", made.GetProperty("source").GetString());

        var only = Assert.Single(await ListAsync(client));
        Assert.Equal("SHOP", only.GetProperty("name").GetString());
    }

    /// The whole point: two people on one studio do not see each other's databases.
    [Fact]
    public async Task A_second_browser_sees_none_of_it()
    {
        using var factory = Factory();
        using var mine = factory.CreateClient();
        using var theirs = factory.CreateClient();

        await AddAsync(mine, "MINE");

        Assert.Single(await ListAsync(mine));
        Assert.Empty(await ListAsync(theirs));
    }

    /// Nothing on disk is the reason a connection string in a session is safer than one in the
    /// store: the file the store lives in stays empty.
    [Fact]
    public async Task Nothing_is_written_to_the_store()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        await AddAsync(client, "SHOP");

        var store = factory.Services.GetRequiredService<ConnectionStore>();

        Assert.Empty(store.List());
    }

    [Fact]
    public async Task The_browser_that_made_it_may_delete_it()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var made = await AddAsync(client, "SHOP");
        var id = made.GetProperty("id").GetString()!;

        var deleted = await client.DeleteAsync($"/api/connections/{id}", Ct);

        deleted.EnsureSuccessStatusCode();
        Assert.Empty(await ListAsync(client));
    }

    [Fact]
    public async Task The_browser_that_made_it_may_rename_it()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var made = await AddAsync(client, "SHOP");
        var id = made.GetProperty("id").GetString()!;

        var renamed = await client.PutAsJsonAsync($"/api/connections/{id}", Connection("SALES"), Ct);
        renamed.EnsureSuccessStatusCode();

        var only = Assert.Single(await ListAsync(client));
        Assert.Equal("SALES", only.GetProperty("name").GetString());
    }

    /// Somebody else's connection does not exist for you — a 404 rather than a refusal, because a
    /// refusal would confirm that it is there.
    [Fact]
    public async Task Another_browser_cannot_delete_it()
    {
        using var factory = Factory();
        using var mine = factory.CreateClient();
        using var theirs = factory.CreateClient();

        var made = await AddAsync(mine, "MINE");
        var id = made.GetProperty("id").GetString()!;

        var deleted = await theirs.DeleteAsync($"/api/connections/{id}", Ct);

        Assert.Equal(HttpStatusCode.NotFound, deleted.StatusCode);
        Assert.Single(await ListAsync(mine));
    }

    [Fact]
    public async Task An_uploaded_file_is_a_session_connection_too()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(RealSqlite()), "file", "shop.sqlite3");

        var answer = await client.PostAsync("/api/connections/file", body, Ct);
        answer.EnsureSuccessStatusCode();

        using var made = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(Ct));

        Assert.Equal("Session", made.RootElement.GetProperty("source").GetString());
        Assert.Empty(factory.Services.GetRequiredService<ConnectionStore>().List());
    }

    /// And it opens: an uploaded database in a session is a database, not a placeholder.
    [Fact]
    public async Task The_studio_reads_an_uploaded_session_database()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(RealSqlite()), "file", "shop.sqlite3");

        var answer = await client.PostAsync("/api/connections/file", body, Ct);
        answer.EnsureSuccessStatusCode();

        using var made = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(Ct));
        var id = made.RootElement.GetProperty("id").GetString()!;

        using var tree = JsonDocument.Parse(await client.GetStringAsync($"/api/schema/{id}", Ct));

        Assert.NotEmpty(tree.RootElement.EnumerateArray());
    }

    /// A file a session owns lives under its own folder, so the sweeper can take the lot at once.
    [Fact]
    public async Task An_uploaded_session_file_lives_under_the_sessions_folder()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(RealSqlite()), "file", "shop.sqlite3");

        (await client.PostAsync("/api/connections/file", body, Ct)).EnsureSuccessStatusCode();

        var sessions = Path.Combine(_dir, "data", "files", "session");

        Assert.True(Directory.Exists(sessions));
        Assert.NotEmpty(Directory.GetFiles(sessions, "shop.sqlite3", SearchOption.AllDirectories));
    }

    /// In stored scope everything is where it was: the same call, the same store, the same folder.
    [Fact]
    public async Task Stored_scope_is_unchanged()
    {
        var config = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "stored", "wds.db"),
        };

        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));

        using var mine = factory.CreateClient();
        using var theirs = factory.CreateClient();

        var made = await AddAsync(mine, "SHOP");

        Assert.Equal("Stored", made.GetProperty("source").GetString());
        // Stored means everybody sees it, which is exactly what session scope does not do.
        Assert.Single(await ListAsync(theirs));
        Assert.Single(factory.Services.GetRequiredService<ConnectionStore>().List());
    }

    /// Importing is the form with a file in front of it, and lands wherever the form lands.
    [Fact]
    public async Task An_import_lands_in_the_session_too()
    {
        using var factory = Factory();
        using var mine = factory.CreateClient();
        using var theirs = factory.CreateClient();

        var answer = await mine.PostAsJsonAsync("/api/connections/import", new[]
        {
            new { name = "SHOP", engine = "sqlite", readOnly = true, host = "", database = "" },
        }, Ct);

        answer.EnsureSuccessStatusCode();

        Assert.Single(await ListAsync(mine));
        Assert.Empty(await ListAsync(theirs));
        Assert.Empty(factory.Services.GetRequiredService<ConnectionStore>().List());
    }

    /// A `?u=` link and the form now agree: in session scope both belong to the browser.
    [Fact]
    public async Task A_link_and_the_form_end_up_in_the_same_place()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "connection-string"));
        using var client = factory.CreateClient();

        await AddAsync(client, "TYPED");

        var opened = await client.PostAsJsonAsync("/api/connections/from-url",
            new { u = "LINKED:Data Source=:memory:" }, Ct);

        opened.EnsureSuccessStatusCode();

        var names = (await ListAsync(client))
            .Select(c => c.GetProperty("name").GetString())
            .OrderBy(n => n)
            .ToList();

        Assert.Equal(["LINKED", "TYPED"], names);
    }

    private static byte[] RealSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wds-session-{Guid.NewGuid():n}.db");

        try
        {
            using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path}"))
            {
                db.Open();
                using var command = db.CreateCommand();
                command.CommandText = "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT);";
                command.ExecuteNonQuery();
            }

            Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
            return File.ReadAllBytes(path);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
