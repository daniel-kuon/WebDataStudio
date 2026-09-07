using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A file on somebody's machine becomes a connection, and stops being one when the connection goes.
public class FileConnectionEndpointTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-file-upload").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory() =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["DB_PATH"] = Path.Combine(_dir, "wds.db"),
            })));

    /// A real SQLite database, written by the driver that will later open it: a hand-made header is
    /// enough to be recognised and not enough to be queried, and one of these tests is about opening.
    private MultipartFormDataContent Sqlite(string name)
    {
        var source = Path.Combine(_dir, $"source-{Guid.NewGuid():n}.db");

        using (var db = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={source}"))
        {
            db.Open();
            using var command = db.CreateCommand();
            command.CommandText = "CREATE TABLE people (id INTEGER PRIMARY KEY, name TEXT); "
                                  + "INSERT INTO people (name) VALUES ('ada');";
            command.ExecuteNonQuery();
        }

        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();

        var content = new MultipartFormDataContent();
        var file = new ByteArrayContent(File.ReadAllBytes(source));
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        content.Add(file, "file", name);
        return content;
    }

    private static MultipartFormDataContent Text(string name, string body)
    {
        var content = new MultipartFormDataContent();
        content.Add(new ByteArrayContent(Encoding.UTF8.GetBytes(body)), "file", name);
        return content;
    }

    private string[] Uploaded(string pattern) =>
        Directory.Exists(Path.Combine(_dir, "files"))
            ? Directory.GetFiles(Path.Combine(_dir, "files"), pattern, SearchOption.AllDirectories)
            : [];

    [Fact]
    public async Task An_uploaded_file_becomes_a_connection_the_studio_can_see()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", Sqlite("shop.sqlite3"), Ct);
        response.EnsureSuccessStatusCode();

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var id = created.RootElement.GetProperty("id").GetString()!;

        Assert.Equal("sqlite", created.RootElement.GetProperty("engine").GetString());
        Assert.Equal("shop.sqlite3", created.RootElement.GetProperty("name").GetString());

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.Contains(list.RootElement.EnumerateArray(), c => c.GetProperty("id").GetString() == id);

        // The copy is under the data directory, not wherever the browser had it.
        Assert.Single(Uploaded("shop.sqlite3"));
    }

    /// The point of uploading rather than typing a path: the connection works, so the file arrived
    /// whole and the driver opens it.
    [Fact]
    public async Task The_connection_it_makes_actually_opens()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", Sqlite("works.db"), Ct);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var id = created.RootElement.GetProperty("id").GetString()!;

        var health = await client.GetAsync($"/api/connections/{id}/health", Ct);
        health.EnsureSuccessStatusCode();

        using var answer = JsonDocument.Parse(await health.Content.ReadAsStringAsync(Ct));
        Assert.True(answer.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task Deleting_the_connection_deletes_the_file_it_was_given()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", Sqlite("gone.db"), Ct);
        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        var id = created.RootElement.GetProperty("id").GetString()!;

        Assert.Single(Uploaded("gone.db"));

        var deleted = await client.DeleteAsync($"/api/connections/{id}", Ct);
        Assert.Equal(HttpStatusCode.NoContent, deleted.StatusCode);

        Assert.Empty(Uploaded("gone.db"));
    }

    [Fact]
    public async Task A_file_that_is_not_a_database_says_so_and_is_not_kept()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", Text("shop.db", "id,name\n1,ada\n"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("SQLite", await response.Content.ReadAsStringAsync(Ct));
        Assert.Empty(Uploaded("shop.db"));
    }

    [Fact]
    public async Task A_format_that_needs_an_engine_we_do_not_have_is_refused_with_the_reason()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", Text("db.mdf", "x"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("SQL Server", await response.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Something_no_driver_reads_is_refused_by_its_extension()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", Text("holiday.jpg", "x"), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains(".jpg", await response.Content.ReadAsStringAsync(Ct));
    }

    /// A CSV is a storage connection over the folder it was put in, and read-only.
    [Fact]
    public async Task A_data_file_becomes_a_read_only_storage_connection()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", Text("people.csv", "id,name\n1,ada\n"), Ct);
        response.EnsureSuccessStatusCode();

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));

        Assert.Equal("storage", created.RootElement.GetProperty("engine").GetString());
        Assert.True(created.RootElement.GetProperty("readOnly").GetBoolean());
        Assert.Single(Uploaded("people.csv"));
    }

    [Fact]
    public async Task A_name_can_be_given_instead_of_the_file_name()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var content = Sqlite("2026-09-07-export.sqlite3");
        content.Add(new StringContent("Yesterday's export"), "name");

        var response = await client.PostAsync("/api/connections/file", content, Ct);
        response.EnsureSuccessStatusCode();

        using var created = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        Assert.Equal("Yesterday's export", created.RootElement.GetProperty("name").GetString());
    }

    [Fact]
    public async Task Nothing_sent_is_a_sentence_rather_than_a_stack()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var response = await client.PostAsync("/api/connections/file", new MultipartFormDataContent(), Ct);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Contains("no file", await response.Content.ReadAsStringAsync(Ct));
    }

    /// Two files of the same name are two connections, each with its own copy — the id names the
    /// folder, so the second upload does not overwrite the first one's database.
    [Fact]
    public async Task The_same_name_twice_is_two_connections_with_two_files()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var first = await client.PostAsync("/api/connections/file", Sqlite("same.db"), Ct);
        var second = await client.PostAsync("/api/connections/file", Sqlite("same.db"), Ct);

        first.EnsureSuccessStatusCode();
        second.EnsureSuccessStatusCode();

        using var created = JsonDocument.Parse(await second.Content.ReadAsStringAsync(Ct));

        // A name is how a connection is addressed, so the second one is numbered rather than refused.
        Assert.Equal("same.db (2)", created.RootElement.GetProperty("name").GetString());
        Assert.Equal(2, Uploaded("same.db").Length);
    }
}
