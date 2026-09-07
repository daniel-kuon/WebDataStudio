using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A studio that is open for weeks needs its sessions to end: a lifetime, a ceiling, and a button
/// for somebody who is done now.
public class SessionLifetimeTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-session-life").FullName;
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

    private static ConnectionSpec Spec(string id, string name) =>
        new(id, name, "sqlite", "Data Source=:memory:", true, null, null, ConnectionSource.Session);

    private static object Connection(string name) => new
    {
        name,
        engine = "sqlite",
        connectionString = "Data Source=:memory:",
        readOnly = true,
    };

    // --- the lifetime ----------------------------------------------------------------------------

    [Fact]
    public void A_session_that_has_gone_quiet_for_longer_than_the_lifetime_is_swept()
    {
        using var factory = Factory(("WDS_SESSION_TTL_MINUTES", "30"));
        _ = factory.CreateClient();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        var now = DateTimeOffset.UtcNow;

        sessions.Remember("quiet", now.AddHours(-2));
        sessions.Add("quiet", Spec("s1", "OLD"));

        sessions.Remember("busy", now.AddMinutes(-1));
        sessions.Add("busy", Spec("s2", "RECENT"));

        var swept = sessions.Sweep(now);

        Assert.Equal(1, swept);
        Assert.Empty(sessions.For("quiet"));
        Assert.Single(sessions.For("busy"));
    }

    [Fact]
    public void Without_a_lifetime_nothing_is_swept()
    {
        using var factory = Factory(("WDS_SESSION_TTL_MINUTES", "0"));
        _ = factory.CreateClient();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();

        sessions.Remember("ancient", DateTimeOffset.UtcNow.AddDays(-40));
        sessions.Add("ancient", Spec("s1", "OLD"));

        Assert.Equal(0, sessions.Sweep(DateTimeOffset.UtcNow));
        Assert.Single(sessions.For("ancient"));
    }

    /// A request is what keeps a session alive, so somebody who is still looking at a table does not
    /// lose it under them.
    [Fact]
    public async Task A_request_keeps_a_session_alive()
    {
        using var factory = Factory(("WDS_SESSION_TTL_MINUTES", "30"));
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections", Connection("SHOP"), Ct);
        answer.EnsureSuccessStatusCode();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        var key = sessions.Keys.Single();

        // Pretend the session has been idle, then knock once: it must survive the next sweep.
        sessions.Remember(key, DateTimeOffset.UtcNow.AddHours(-2));
        await client.GetAsync("/api/connections", Ct);

        Assert.Equal(0, sessions.Sweep(DateTimeOffset.UtcNow));
        Assert.Single(sessions.For(key));
    }

    /// What a visitor uploaded goes with their session — a studio anybody can reach must not keep
    /// files for everybody who ever visited.
    [Fact]
    public async Task Sweeping_takes_the_files_that_session_brought()
    {
        using var factory = Factory(("WDS_SESSION_TTL_MINUTES", "30"));
        using var client = factory.CreateClient();

        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(RealSqlite()), "file", "shop.sqlite3");
        (await client.PostAsync("/api/connections/file", body, Ct)).EnsureSuccessStatusCode();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        var key = sessions.Keys.Single();
        var folder = Path.Combine(_dir, "data", "files", "session", SessionConnections.FolderFor(key));

        Assert.True(Directory.Exists(folder));

        sessions.Remember(key, DateTimeOffset.UtcNow.AddHours(-2));
        sessions.Sweep(DateTimeOffset.UtcNow);

        Assert.False(Directory.Exists(folder));
    }

    // --- the ceiling -----------------------------------------------------------------------------

    [Fact]
    public async Task The_ceiling_says_how_many_one_browser_may_hold()
    {
        using var factory = Factory(("WDS_SESSION_MAX_CONNECTIONS", "2"));
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/connections", Connection("ONE"), Ct)).EnsureSuccessStatusCode();
        (await client.PostAsJsonAsync("/api/connections", Connection("TWO"), Ct)).EnsureSuccessStatusCode();

        var third = await client.PostAsJsonAsync("/api/connections", Connection("THREE"), Ct);

        Assert.Equal(HttpStatusCode.Conflict, third.StatusCode);

        var said = await third.Content.ReadAsStringAsync(Ct);
        Assert.Contains("WDS_SESSION_MAX_CONNECTIONS", said);
        // The number, so it reads as a limit rather than as a bug.
        Assert.Contains("2", said);
    }

    /// Replacing one of your own is not adding a third.
    [Fact]
    public async Task Renaming_at_the_ceiling_still_works()
    {
        using var factory = Factory(("WDS_SESSION_MAX_CONNECTIONS", "1"));
        using var client = factory.CreateClient();

        var made = await client.PostAsJsonAsync("/api/connections", Connection("ONE"), Ct);
        made.EnsureSuccessStatusCode();

        using var body = JsonDocument.Parse(await made.Content.ReadAsStringAsync(Ct));
        var id = body.RootElement.GetProperty("id").GetString()!;

        var renamed = await client.PutAsJsonAsync($"/api/connections/{id}", Connection("TWO"), Ct);

        renamed.EnsureSuccessStatusCode();
    }

    // --- being done now --------------------------------------------------------------------------

    [Fact]
    public async Task Forgetting_a_session_empties_it_and_clears_the_cookie()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        (await client.PostAsJsonAsync("/api/connections", Connection("SHOP"), Ct)).EnsureSuccessStatusCode();

        var forgotten = await client.PostAsync("/api/connections/forget", null, Ct);
        forgotten.EnsureSuccessStatusCode();

        Assert.Contains(forgotten.Headers.GetValues("Set-Cookie"),
            cookie => cookie.StartsWith("wds_session=", StringComparison.Ordinal)
                      && cookie.Contains("expires=Thu, 01 Jan 1970", StringComparison.OrdinalIgnoreCase));

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        Assert.All(sessions.Keys, key => Assert.Empty(sessions.For(key)));
    }

    [Fact]
    public async Task Forgetting_takes_the_files_with_it()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(RealSqlite()), "file", "shop.sqlite3");
        (await client.PostAsync("/api/connections/file", body, Ct)).EnsureSuccessStatusCode();

        var sessions = factory.Services.GetRequiredService<SessionConnections>();
        var folder = Path.Combine(_dir, "data", "files", "session",
            SessionConnections.FolderFor(sessions.Keys.Single()));

        (await client.PostAsync("/api/connections/forget", null, Ct)).EnsureSuccessStatusCode();

        Assert.False(Directory.Exists(folder));
    }

    // --- one visitor's file is not another's ------------------------------------------------------

    /// The folders are named with GUIDs and are not guessable, but a path that leaked would
    /// otherwise hand one visitor another visitor's database. So `?u=` does not reach into the
    /// uploads tree at all.
    [Fact]
    public async Task A_link_cannot_name_a_file_inside_the_uploads_tree()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "file"));
        using var client = factory.CreateClient();

        using var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(RealSqlite()), "file", "shop.sqlite3");
        (await client.PostAsync("/api/connections/file", body, Ct)).EnsureSuccessStatusCode();

        var uploaded = Directory
            .GetFiles(Path.Combine(_dir, "data", "files"), "shop.sqlite3", SearchOption.AllDirectories)
            .Single();

        using var second = factory.CreateClient();
        var answer = await second.PostAsJsonAsync("/api/connections/from-url", new { u = uploaded }, Ct);
        answer.EnsureSuccessStatusCode();

        using var opened = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(Ct));
        var only = opened.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Contains("belongs to somebody", only.GetProperty("refused").GetString());
    }

    private static byte[] RealSqlite()
    {
        var path = Path.Combine(Path.GetTempPath(), $"wds-life-{Guid.NewGuid():n}.db");

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
