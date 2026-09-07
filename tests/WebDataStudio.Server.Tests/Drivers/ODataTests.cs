using System.Net;
using System.Text;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Drivers.OData;
using WebDataStudio.Server.Models;
using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Tests.Drivers;

/// An OData service is HTTP, so the tests speak to a canned one: no container, no network.
public sealed class FakeODataHandler : HttpMessageHandler
{
    public List<HttpRequestMessage> Requests { get; } = [];

    private const string Metadata = """
        <?xml version="1.0" encoding="utf-8"?>
        <edmx:Edmx Version="4.0" xmlns:edmx="http://docs.oasis-open.org/odata/ns/edmx">
          <edmx:DataServices>
            <Schema Namespace="Shop" xmlns="http://docs.oasis-open.org/odata/ns/edm">
              <EntityType Name="Base" Abstract="true">
                <Key><PropertyRef Name="Id"/></Key>
                <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
              </EntityType>
              <EntityType Name="Person" BaseType="Shop.Base">
                <Property Name="Name" Type="Edm.String" Nullable="false"/>
                <Property Name="Active" Type="Edm.Boolean"/>
                <NavigationProperty Name="Orders" Type="Collection(Shop.Order)"/>
              </EntityType>
              <EntityType Name="Order">
                <Key><PropertyRef Name="Id"/></Key>
                <Property Name="Id" Type="Edm.Int32" Nullable="false"/>
                <Property Name="Total" Type="Edm.Double"/>
              </EntityType>
              <EntityContainer Name="Container">
                <EntitySet Name="People" EntityType="Shop.Person"/>
                <EntitySet Name="Orders" EntityType="Shop.Order"/>
                <EntitySet Name="Legacy" EntityType="Shop.Order"/>
              </EntityContainer>
            </Schema>
          </edmx:DataServices>
        </edmx:Edmx>
        """;

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Requests.Add(request);
        var path = request.RequestUri!.PathAndQuery;

        (HttpStatusCode Status, string Body, string Type) answer = path switch
        {
            "/svc/$metadata" => (HttpStatusCode.OK, Metadata, "application/xml"),
            "/svc/People" => (HttpStatusCode.OK, """
                { "value": [ { "Id": 1, "Name": "ada", "Active": true },
                             { "Id": 2, "Name": "linus", "Active": true } ],
                  "@odata.nextLink": "People?$skiptoken=2" }
                """, "application/json"),
            "/svc/People?$skiptoken=2" => (HttpStatusCode.OK, """
                { "value": [ { "Id": 3, "Name": "grace", "Active": false } ] }
                """, "application/json"),
            "/svc/People/$count" => (HttpStatusCode.OK, "3", "text/plain"),
            "/svc/People(1)" => (HttpStatusCode.OK,
                """{ "@odata.context": "$metadata#People/$entity", "@odata.etag": "W/\"1\"", "Id": 1, "Name": "ada", "Active": true }""",
                "application/json"),
            "/svc/Legacy" => (HttpStatusCode.OK, """
                { "d": { "results": [ { "Id": 9, "Total": 1.5 } ] } }
                """, "application/json"),
            "/svc/Orders/$count" => (HttpStatusCode.InternalServerError, "no count here", "text/plain"),
            _ => (HttpStatusCode.NotFound, """{ "error": { "message": "Resource not found for the segment." } }""",
                "application/json"),
        };

        return Task.FromResult(new HttpResponseMessage(answer.Status)
        {
            Content = new StringContent(answer.Body, Encoding.UTF8, answer.Type),
        });
    }
}

public class ODataDriverTests
{
    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static ConnectionSpec Spec(string url = "https://example.test/svc/") =>
        new("t", "test", "odata", url, false, null, null, ConnectionSource.Stored);

    private static (ODataDriver Driver, FakeODataHandler Handler) Make()
    {
        var handler = new FakeODataHandler();
        return (new ODataDriver(handler), handler);
    }

    private static async Task<List<ResultChunk>> RunAsync(ODataDriver driver, IDbSession session, string query,
        int maxRows = 1000)
    {
        var chunks = new List<ResultChunk>();
        await foreach (var chunk in driver.ExecuteAsync(session, new ScriptRequest(query, maxRows, 30), Ct))
            chunks.Add(chunk);
        return chunks;
    }

    [Fact]
    public void Declares_itself_as_a_read_only_non_sql_engine()
    {
        var (driver, _) = Make();
        Assert.False(driver.Caps.Sql);
        Assert.False(driver.Caps.TabularBrowse);
        Assert.True(driver.Dialect.IsReadOnlyStatement("People?$top=1"));
    }

    [Fact]
    public async Task Opening_reads_the_metadata_and_lists_entity_sets_as_tables()
    {
        var (driver, handler) = Make();
        await using var session = await driver.OpenAsync(Spec(), Ct);

        Assert.Contains(handler.Requests, r => r.RequestUri!.PathAndQuery == "/svc/$metadata");

        var root = await driver.IntrospectAsync(session, null, Ct);
        Assert.Equal(["Legacy", "Orders", "People"], root.Select(n => n.Label).ToArray());
        Assert.All(root, n => Assert.Equal(SchemaNodeKind.Table, n.Ref.Kind));
    }

    [Fact]
    public async Task Describes_properties_including_inherited_ones_with_the_key_marked()
    {
        var (driver, _) = Make();
        await using var session = await driver.OpenAsync(Spec(), Ct);
        var detail = await driver.DescribeAsync(session, new SchemaNodeRef(SchemaNodeKind.Table, ["People"]), Ct);

        Assert.Equal(["Id", "Name", "Active"], detail.Columns.Select(c => c.Name).ToArray());
        Assert.Contains(detail.Columns,
            c => c is { Name: "Id", IsPrimaryKey: true, Nullable: false, DataType: "Edm.Int32" });
        Assert.Contains(detail.Columns, c => c is { Name: "Active", Nullable: true });
        Assert.Equal(3, detail.RowCount);
    }

    [Fact]
    public async Task A_failing_count_leaves_the_row_count_unknown()
    {
        var (driver, _) = Make();
        await using var session = await driver.OpenAsync(Spec(), Ct);
        var detail = await driver.DescribeAsync(session, new SchemaNodeRef(SchemaNodeKind.Table, ["Orders"]), Ct);
        Assert.Null(detail.RowCount);
    }

    [Fact]
    public async Task Follows_the_next_link_and_returns_every_entity()
    {
        var (driver, _) = Make();
        await using var session = await driver.OpenAsync(Spec(), Ct);
        var chunks = await RunAsync(driver, session, "People");

        var documents = chunks.OfType<ResultChunk.Documents>().SelectMany(d => d.Items).ToList();
        Assert.Equal([1, 2, 3], documents.Select(d => d.GetProperty("Id").GetInt32()).ToArray());
        var end = Assert.Single(chunks.OfType<ResultChunk.End>());
        Assert.False(end.Truncated);
    }

    [Fact]
    public async Task Stops_following_pages_at_the_row_limit()
    {
        var (driver, handler) = Make();
        await using var session = await driver.OpenAsync(Spec(), Ct);
        var chunks = await RunAsync(driver, session, "People", maxRows: 2);

        Assert.Equal(2, chunks.OfType<ResultChunk.Documents>().Sum(d => d.Items.Count));
        Assert.True(Assert.Single(chunks.OfType<ResultChunk.End>()).Truncated);
        Assert.DoesNotContain(handler.Requests, r => r.RequestUri!.Query.Contains("skiptoken"));
    }

    [Fact]
    public async Task Reads_the_verbose_json_of_an_odata_v2_service()
    {
        var (driver, _) = Make();
        await using var session = await driver.OpenAsync(Spec(), Ct);
        var chunks = await RunAsync(driver, session, "Legacy");

        var document = Assert.Single(chunks.OfType<ResultChunk.Documents>().SelectMany(d => d.Items));
        Assert.Equal(9, document.GetProperty("Id").GetInt32());
    }

    [Fact]
    public async Task A_single_entity_is_one_document_without_its_control_information()
    {
        var (driver, _) = Make();
        await using var session = await driver.OpenAsync(Spec(), Ct);
        var chunks = await RunAsync(driver, session, "People(1)");

        var document = Assert.Single(chunks.OfType<ResultChunk.Documents>().SelectMany(d => d.Items));
        Assert.Equal(["Id", "Name", "Active"], document.EnumerateObject().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task A_count_request_comes_back_as_a_number()
    {
        var (driver, _) = Make();
        await using var session = await driver.OpenAsync(Spec(), Ct);
        var chunks = await RunAsync(driver, session, "People/$count");

        var document = Assert.Single(chunks.OfType<ResultChunk.Documents>().SelectMany(d => d.Items));
        Assert.Equal(3, document.GetProperty("count").GetInt32());
    }

    [Fact]
    public async Task An_http_error_becomes_an_error_chunk_with_the_service_message()
    {
        var (driver, _) = Make();
        await using var session = await driver.OpenAsync(Spec(), Ct);
        var chunks = await RunAsync(driver, session, "Nowhere");

        var error = Assert.Single(chunks.OfType<ResultChunk.Error>());
        Assert.Contains("Resource not found", error.Text);
        Assert.Equal("404", error.Code);
    }

    [Fact]
    public async Task Credentials_in_the_url_become_basic_auth_and_leave_the_request_url()
    {
        var (driver, handler) = Make();
        await using var _ = await driver.OpenAsync(Spec("https://ada:p%40ss@example.test/svc/"), Ct);

        var request = handler.Requests[0];
        Assert.Equal("", request.RequestUri!.UserInfo);
        Assert.Equal("Basic", request.Headers.Authorization!.Scheme);
        Assert.Equal(Convert.ToBase64String("ada:p@ss"u8), request.Headers.Authorization.Parameter);
    }

    [Fact]
    public async Task A_bearer_user_sends_the_token_as_a_bearer_header()
    {
        var (driver, handler) = Make();
        await using var _ = await driver.OpenAsync(Spec("https://bearer:tok123@example.test/svc/"), Ct);

        Assert.Equal("Bearer", handler.Requests[0].Headers.Authorization!.Scheme);
        Assert.Equal("tok123", handler.Requests[0].Headers.Authorization!.Parameter);
    }

    [Fact]
    public async Task A_root_without_a_trailing_slash_still_resolves_relative_paths()
    {
        var (driver, handler) = Make();
        await using var session = await driver.OpenAsync(Spec("https://example.test/svc"), Ct);
        await RunAsync(driver, session, "People/$count");
        Assert.Contains(handler.Requests, r => r.RequestUri!.PathAndQuery == "/svc/People/$count");
    }

    [Fact]
    public async Task A_service_that_is_not_odata_fails_to_open()
    {
        var (driver, _) = Make();
        await Assert.ThrowsAnyAsync<Exception>(() => driver.OpenAsync(Spec("https://example.test/elsewhere/"), Ct));
    }

    [Fact]
    public void Http_urls_are_recognised_as_odata()
    {
        Assert.Equal("odata", ConnectionUrl.EngineFromScheme("https"));
        Assert.Equal("odata", ConnectionUrl.EngineFromScheme("http"));
        Assert.Equal("odata",
            EngineGuess.FromConnectionString("https://services.odata.org/V4/Northwind/Northwind.svc/"));
        Assert.Equal("https://h/svc/", ConnectionUrl.ToAdoConnectionString("odata", new Uri("https://h/svc/")));
    }
}
