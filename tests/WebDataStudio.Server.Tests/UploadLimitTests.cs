using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;

namespace WebDataStudio.Server.Tests;

/// A size a visitor may not exceed. Before this the only cap was Kestrel's own 30 MB, which was an
/// accident rather than a decision — and it answered with a status code nobody wrote.
public class UploadLimitTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-upload-cap").FullName;
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

    /// A SQLite database of about the size asked for: a real header, then pages of nothing.
    private static byte[] Sqlite(int kilobytes)
    {
        var bytes = new byte[kilobytes * 1024];
        "SQLite format 3\0"u8.ToArray().CopyTo(bytes, 0);

        return bytes;
    }

    private static MultipartFormDataContent Upload(byte[] bytes)
    {
        var body = new MultipartFormDataContent();
        body.Add(new ByteArrayContent(bytes), "file", "shop.sqlite3");

        return body;
    }

    [Fact]
    public async Task A_file_over_the_cap_is_refused_with_the_setting_that_sets_it()
    {
        using var factory = Factory(("WDS_UPLOAD_MAX_MB", "1"));
        using var client = factory.CreateClient();

        using var body = Upload(Sqlite(1536));
        var answer = await client.PostAsync("/api/connections/file", body, Ct);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, answer.StatusCode);

        var said = await answer.Content.ReadAsStringAsync(Ct);
        Assert.Contains("WDS_UPLOAD_MAX_MB", said);
        Assert.Contains("1", said);
    }

    /// Refused and nothing kept: half a database on the disk of a studio anybody can reach is the
    /// thing this setting exists to prevent.
    [Fact]
    public async Task A_refused_file_leaves_nothing_behind()
    {
        using var factory = Factory(("WDS_UPLOAD_MAX_MB", "1"));
        using var client = factory.CreateClient();

        using var body = Upload(Sqlite(1536));
        await client.PostAsync("/api/connections/file", body, Ct);

        var files = Path.Combine(_dir, "data", "files");
        var kept = Directory.Exists(files)
            ? Directory.GetFiles(files, "*", SearchOption.AllDirectories)
            : [];

        Assert.Empty(kept);
    }

    [Fact]
    public async Task A_file_under_the_cap_is_taken()
    {
        using var factory = Factory(("WDS_UPLOAD_MAX_MB", "2"));
        using var client = factory.CreateClient();

        using var body = Upload(Sqlite(512));
        var answer = await client.PostAsync("/api/connections/file", body, Ct);

        answer.EnsureSuccessStatusCode();
    }

    /// The default is a hundred megabytes, so a database that is merely large arrives. Kestrel's own
    /// limit is raised to match in Program.cs; the test server has no Kestrel to check that with.
    [Fact]
    public async Task The_default_takes_a_file_larger_than_thirty_megabytes()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        using var body = Upload(Sqlite(40 * 1024));
        var answer = await client.PostAsync("/api/connections/file", body, Ct);

        answer.EnsureSuccessStatusCode();
    }
}
