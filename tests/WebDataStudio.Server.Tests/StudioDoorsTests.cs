using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A door a deployment closed answers with the setting that would open it. And a studio that closed
/// nothing has every way in it always had — which is what most of this file is about.
public class StudioDoorsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-doors").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var config = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db"),
        };

        foreach (var (key, value) in settings) config[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));
    }

    private static object Connection(string name) => new
    {
        name,
        engine = "sqlite",
        connectionString = "Data Source=:memory:",
        readOnly = true,
    };

    private static MultipartFormDataContent Upload()
    {
        var body = new MultipartFormDataContent();
        var file = new ByteArrayContent("SQLite format 3\0"u8.ToArray());
        body.Add(file, "file", "shop.sqlite3");

        return body;
    }

    // --- the form, and testing one ---------------------------------------------------------------

    [Fact]
    public async Task Adding_a_connection_is_refused_with_the_setting_that_would_allow_it()
    {
        using var factory = Factory(("WDS_ALLOW_ADD_CONNECTION", "false"));
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections", Connection("SHOP"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Contains("WDS_ALLOW_ADD_CONNECTION", await answer.Content.ReadAsStringAsync(Ct));
    }

    /// The cheapest door of the four: it opens an arbitrary connection and keeps nothing, so gating
    /// the form and leaving this open would be a lock on an open frame.
    [Fact]
    public async Task Testing_a_connection_is_behind_the_same_setting()
    {
        using var factory = Factory(("WDS_ALLOW_ADD_CONNECTION", "false"));
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections/test", Connection("SHOP"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Contains("WDS_ALLOW_ADD_CONNECTION", await answer.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task Importing_connections_is_behind_the_same_setting()
    {
        using var factory = Factory(("WDS_ALLOW_ADD_CONNECTION", "false"));
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections/import",
            new[] { new { name = "SHOP", engine = "sqlite", readOnly = true } }, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Contains("WDS_ALLOW_ADD_CONNECTION", await answer.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task With_nothing_configured_a_connection_can_still_be_added()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections", Connection("SHOP"), Ct);

        answer.EnsureSuccessStatusCode();
    }

    // --- a file from the browser -----------------------------------------------------------------

    [Fact]
    public async Task Uploading_a_file_is_refused_with_the_setting_that_would_allow_it()
    {
        using var factory = Factory(("WDS_ALLOW_FILE_UPLOAD", "false"));
        using var client = factory.CreateClient();

        using var body = Upload();
        var answer = await client.PostAsync("/api/connections/file", body, Ct);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Contains("WDS_ALLOW_FILE_UPLOAD", await answer.Content.ReadAsStringAsync(Ct));
    }

    /// Refused before the body is read: a closed door should not first take a database off somebody.
    [Fact]
    public async Task A_refused_upload_keeps_nothing()
    {
        using var factory = Factory(("WDS_ALLOW_FILE_UPLOAD", "false"));
        using var client = factory.CreateClient();

        using var body = Upload();
        await client.PostAsync("/api/connections/file", body, Ct);

        var files = Path.Combine(_dir, "data", "files");

        Assert.False(Directory.Exists(files) && Directory.GetFiles(files, "*", SearchOption.AllDirectories).Length > 0);
    }

    // --- the server's own folders ----------------------------------------------------------------

    [Fact]
    public async Task Browsing_the_server_is_refused_with_the_setting_that_would_allow_it()
    {
        using var factory = Factory(("WDS_ALLOW_FILE_BROWSE", "false"));
        using var client = factory.CreateClient();

        var answer = await client.GetAsync("/api/connections/browse", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Contains("WDS_ALLOW_FILE_BROWSE", await answer.Content.ReadAsStringAsync(Ct));
    }

    /// Closed for the listing *and* for a path somebody guessed, which is the same door.
    [Fact]
    public async Task A_closed_browser_is_closed_for_a_named_folder_too()
    {
        using var factory = Factory(("WDS_ALLOW_FILE_BROWSE", "false"));
        using var client = factory.CreateClient();

        var answer = await client.GetAsync(
            $"/api/connections/browse?path={Uri.EscapeDataString(Path.Combine(_dir, "data"))}", Ct);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Contains("WDS_ALLOW_FILE_BROWSE", await answer.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task With_nothing_configured_the_roots_are_still_listed()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var answer = await client.GetAsync("/api/connections/browse", Ct);

        answer.EnsureSuccessStatusCode();
    }

    /// One door closing leaves the others where they were.
    [Fact]
    public async Task Closing_one_door_does_not_close_another()
    {
        using var factory = Factory(("WDS_ALLOW_FILE_BROWSE", "false"));
        using var client = factory.CreateClient();

        var added = await client.PostAsJsonAsync("/api/connections", Connection("SHOP"), Ct);
        added.EnsureSuccessStatusCode();

        using var body = Upload();
        var uploaded = await client.PostAsync("/api/connections/file", body, Ct);

        Assert.NotEqual(HttpStatusCode.Forbidden, uploaded.StatusCode);
    }
}
