using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A link that points at a database file: fetched, capped, and only from hosts the deployment named.
public class UrlDownloadTests : IAsyncLifetime
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-url-download").FullName;
    private WebApplication? _origin;
    private string _originUrl = "";
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync()
    {
        var small = new byte[4096];
        Encoding.ASCII.GetBytes("SQLite format 3\0").CopyTo(small, 0);

        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));

        _origin = builder.Build();

        _origin.MapGet("/shop.sqlite3", () => Results.Bytes(small, "application/octet-stream"));

        // Larger than the cap the test sets, and sent without a declared length so the copy itself
        // has to notice.
        _origin.MapGet("/huge.sqlite3", async (HttpContext ctx) =>
        {
            ctx.Response.ContentType = "application/octet-stream";
            var chunk = new byte[256 * 1024];

            for (var i = 0; i < 12; i++) await ctx.Response.Body.WriteAsync(chunk, ctx.RequestAborted);
        });

        await _origin.StartAsync();
        _originUrl = _origin.Urls.First();
    }

    public async ValueTask DisposeAsync()
    {
        if (_origin is not null) await _origin.DisposeAsync();
        TestDirectory.Remove(_dir);
    }

    private string UrlFolder => Path.Combine(_dir, "data", "files", "url");

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var config = new Dictionary<string, string?> { ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db") };
        foreach (var (key, value) in settings) config[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));
    }

    private static async Task<JsonElement> OpenAsync(HttpClient client, string u)
    {
        var response = await client.PostAsJsonAsync("/api/connections/from-url", new { u }, Ct);
        response.EnsureSuccessStatusCode();
        using var answer = JsonDocument.Parse(await response.Content.ReadAsStringAsync(Ct));
        return answer.RootElement.GetProperty("opened").EnumerateArray().Single().Clone();
    }

    [Fact]
    public async Task A_download_from_a_named_host_becomes_a_connection()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "127.0.0.1"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/shop.sqlite3");

        Assert.Null(opened.GetProperty("refused").GetString());

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.Equal("sqlite", list.RootElement.EnumerateArray().Single().GetProperty("engine").GetString());

        // The copy is the studio's own, not a stream it re-reads on every query.
        Assert.NotEmpty(Directory.GetFiles(UrlFolder, "*.sqlite3", SearchOption.AllDirectories));
    }

    /// The same link twice is one connection and one download.
    [Fact]
    public async Task The_same_link_is_fetched_once()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "127.0.0.1"));
        using var client = factory.CreateClient();

        await OpenAsync(client, $"{_originUrl}/shop.sqlite3");
        await OpenAsync(client, $"{_originUrl}/shop.sqlite3");

        Assert.Single(Directory.GetFiles(UrlFolder, "*.sqlite3", SearchOption.AllDirectories));

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        Assert.Equal(1, list.RootElement.GetArrayLength());
    }

    [Fact]
    public async Task Without_a_host_list_a_download_is_refused()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/shop.sqlite3");

        Assert.Contains("WDS_OPEN_FROM_URL_HOSTS", opened.GetProperty("refused").GetString());
    }

    [Fact]
    public async Task A_host_that_is_not_in_the_list_is_refused()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "data.example"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/shop.sqlite3");

        Assert.Contains("127.0.0.1", opened.GetProperty("refused").GetString());
    }

    [Fact]
    public async Task A_file_larger_than_the_cap_is_refused_rather_than_kept()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "127.0.0.1"), ("WDS_OPEN_FROM_URL_MAX_MB", "1"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/huge.sqlite3");

        Assert.Contains("WDS_OPEN_FROM_URL_MAX_MB", opened.GetProperty("refused").GetString());

        var kept = Directory.Exists(UrlFolder)
            ? Directory.GetFiles(UrlFolder, "*", SearchOption.AllDirectories)
            : [];

        Assert.Empty(kept);
    }

    [Fact]
    public async Task A_url_that_answers_nothing_useful_says_so()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "127.0.0.1"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/not-there.sqlite3");

        Assert.Contains("404", opened.GetProperty("refused").GetString());
    }

    /// A download of something no driver opens is refused before it is fetched.
    [Fact]
    public async Task A_url_that_names_no_database_is_refused_before_fetching()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "download"),
            ("WDS_OPEN_FROM_URL_HOSTS", "127.0.0.1"));
        using var client = factory.CreateClient();

        var opened = await OpenAsync(client, $"{_originUrl}/report.docx");

        Assert.Contains(".docx", opened.GetProperty("refused").GetString());
        Assert.False(Directory.Exists(UrlFolder));
    }
}
