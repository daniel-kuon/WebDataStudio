using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests;

/// A studio anybody may type a connection string into is an outbound connector from wherever it
/// runs. This is the list that says where it may reach — whatever the string says, and wherever the
/// connection came from.
public class ConnectHostsTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("wds-connect-hosts").FullName;
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    public void Dispose() => TestDirectory.Remove(_dir);

    private static ConnectHosts Hosts(string? list) =>
        ConnectHosts.From(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["WDS_CONNECT_HOSTS"] = list })
            .Build());

    // --- reading a host out of a connection string ------------------------------------------------

    [Theory]
    [InlineData("postgresql", "Host=db.example;Database=shop;Username=u;Password=p", "db.example")]
    [InlineData("postgresql", "postgres://u:p@db.example:5432/shop", "db.example")]
    [InlineData("mysql", "Server=db.example;Database=shop;User=u;Password=p", "db.example")]
    [InlineData("sqlserver", "Server=db.example,1433;Database=shop;User Id=sa;Password=p", "db.example")]
    [InlineData("sqlserver", "Data Source=db.example;Initial Catalog=shop;User Id=sa;Password=p", "db.example")]
    [InlineData("mongodb", "mongodb://db.example:27017/events", "db.example")]
    [InlineData("redis", "db.example:6379", "db.example")]
    [InlineData("clickhouse", "Host=db.example;Port=8123", "db.example")]
    public void The_host_is_read_per_engine(string engine, string connectionString, string expected)
    {
        Assert.Equal(expected, ConnectHosts.HostOf(engine, connectionString));
    }

    /// A file is not a network target: a SQLite database has no host to allow or refuse.
    [Theory]
    [InlineData("sqlite", "Data Source=/data/shop.db")]
    [InlineData("duckdb", "Data Source=/data/shop.duckdb")]
    public void A_file_has_no_host(string engine, string connectionString)
    {
        Assert.Null(ConnectHosts.HostOf(engine, connectionString));
        // And is allowed even with a list set, because there is nothing for the list to be about.
        Assert.Null(Hosts("db.example").Refuse(engine, connectionString));
    }

    // --- the list ---------------------------------------------------------------------------------

    [Fact]
    public void An_empty_list_is_no_restriction()
    {
        var hosts = Hosts(null);

        Assert.False(hosts.Restricted);
        Assert.Null(hosts.Refuse("postgresql", "Host=anything.example;Username=u"));
    }

    [Fact]
    public void A_named_host_is_allowed_and_another_is_refused_with_the_setting()
    {
        var hosts = Hosts("db.example, other.example");

        Assert.Null(hosts.Refuse("postgresql", "Host=db.example;Username=u"));

        var refused = hosts.Refuse("postgresql", "Host=elsewhere.example;Username=u");

        Assert.NotNull(refused);
        Assert.Contains("elsewhere.example", refused);
        Assert.Contains("WDS_CONNECT_HOSTS", refused);
    }

    /// The same rule the download list already uses, so the two settings behave alike.
    [Fact]
    public void A_wildcard_matches_one_level_of_subdomain()
    {
        var hosts = Hosts("*.example.com");

        Assert.Null(hosts.Refuse("postgresql", "Host=db.example.com;Username=u"));
        Assert.NotNull(hosts.Refuse("postgresql", "Host=example.com;Username=u"));
        Assert.NotNull(hosts.Refuse("postgresql", "Host=db.other.com;Username=u"));
    }

    /// Guessing would make the list a suggestion.
    [Fact]
    public void A_string_with_no_readable_host_is_refused_while_the_list_is_set()
    {
        var refused = Hosts("db.example").Refuse("postgresql", "Database=shop;Username=u");

        Assert.NotNull(refused);
        Assert.Contains("WDS_CONNECT_HOSTS", refused);
    }

    // --- where it applies -------------------------------------------------------------------------

    private WebApplicationFactory<Program> Factory(params (string Key, string Value)[] settings)
    {
        var config = new Dictionary<string, string?>
        {
            ["DB_PATH"] = Path.Combine(_dir, "data", "wds.db"),
            ["WDS_CONNECT_HOSTS"] = "db.example",
        };

        foreach (var (key, value) in settings) config[key] = value;

        return new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
            b.ConfigureAppConfiguration((_, c) => c.AddInMemoryCollection(config)));
    }

    private static object Connection(string name, string connectionString) => new
    {
        name,
        engine = "postgresql",
        connectionString,
        readOnly = true,
    };

    [Fact]
    public async Task The_form_refuses_a_host_that_is_not_in_the_list()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections",
            Connection("ELSEWHERE", "Host=elsewhere.example;Username=u;Password=p"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Contains("WDS_CONNECT_HOSTS", await answer.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task The_form_takes_a_host_that_is()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections",
            Connection("SHOP", "Host=db.example;Username=u;Password=p"), Ct);

        answer.EnsureSuccessStatusCode();
    }

    /// The cheapest probe of all: it opens whatever the body says. Refused before it opens anything.
    [Fact]
    public async Task Testing_a_connection_is_refused_before_it_reaches_the_network()
    {
        using var factory = Factory();
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections/test",
            Connection("ELSEWHERE", "Host=elsewhere.example;Username=u;Password=p"), Ct);

        Assert.Equal(HttpStatusCode.Forbidden, answer.StatusCode);
        Assert.Contains("WDS_CONNECT_HOSTS", await answer.Content.ReadAsStringAsync(Ct));
    }

    [Fact]
    public async Task A_link_is_refused_the_same_way()
    {
        using var factory = Factory(("WDS_OPEN_FROM_URL", "connection-string"));
        using var client = factory.CreateClient();

        var answer = await client.PostAsJsonAsync("/api/connections/from-url",
            new { u = "postgres://u:p@elsewhere.example:5432/shop" }, Ct);

        answer.EnsureSuccessStatusCode();

        using var opened = JsonDocument.Parse(await answer.Content.ReadAsStringAsync(Ct));
        var only = opened.RootElement.GetProperty("opened").EnumerateArray().Single();

        Assert.Contains("WDS_CONNECT_HOSTS", only.GetProperty("refused").GetString());
    }

    /// A connection the deployment wrote down before the list was set does not become a way around
    /// it: it is not in the list at all.
    [Fact]
    public async Task An_environment_connection_outside_the_list_is_not_offered()
    {
        using var factory = Factory(
            ("WDS_CONN_ALLOWED", "postgres://u:p@db.example:5432/shop"),
            ("WDS_CONN_ELSEWHERE", "postgres://u:p@elsewhere.example:5432/shop"));

        using var client = factory.CreateClient();

        using var list = JsonDocument.Parse(await client.GetStringAsync("/api/connections", Ct));
        var names = list.RootElement.EnumerateArray()
            .Select(c => c.GetProperty("name").GetString())
            .ToList();

        Assert.Contains("ALLOWED", names);
        Assert.DoesNotContain("ELSEWHERE", names);
    }
}
