using System.Data.Common;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Drivers.OData;

/// OData has no SQL; the editor text is a resource path with query options, sent as one GET.
/// Caps.Sql = false is what switches the UI; this exists so shared code keeps compiling.
public sealed class ODataDialect : SqlDialect
{
    public override string QuoteIdentifier(string name) => name;
    public override string ParameterPrefix => "";
    public override string Paginate(string sql, int offset, int limit) => sql;

    /// The driver only ever issues GET, so every statement is a read.
    public override bool IsReadOnlyStatement(string sql) => true;
}

public sealed record ODataProperty(string Name, string Type, bool Nullable, bool IsKey);

/// The part of a CSDL document the studio needs: which entity sets exist and what their entities
/// look like. Namespaces differ between OData versions, so elements are matched by local name.
public sealed class ODataMetadata
{
    public IReadOnlyDictionary<string, IReadOnlyList<ODataProperty>> EntitySets { get; }

    private ODataMetadata(Dictionary<string, IReadOnlyList<ODataProperty>> sets) => EntitySets = sets;

    public static ODataMetadata Parse(string csdl)
    {
        var document = XDocument.Parse(csdl);
        var types = document.Descendants().Where(e => e.Name.LocalName == "EntityType")
            .ToDictionary(e => QualifiedName(e), e => e);

        if (types.Count == 0 && !document.Descendants().Any(e => e.Name.LocalName == "EntityContainer"))
            throw new FormatException("this is not an OData metadata document");

        var sets = new Dictionary<string, IReadOnlyList<ODataProperty>>(StringComparer.Ordinal);
        foreach (var set in document.Descendants().Where(e => e.Name.LocalName == "EntitySet"))
        {
            var name = (string?)set.Attribute("Name");
            var typeName = (string?)set.Attribute("EntityType");
            if (name is null || typeName is null) continue;
            sets[name] = Properties(types, typeName);
        }

        return new ODataMetadata(sets);

        static string QualifiedName(XElement type) =>
            $"{(string?)type.Parent?.Attribute("Namespace")}.{(string?)type.Attribute("Name")}";

        // The EntitySet may name the type through a schema alias; the simple name is the fallback.
        static XElement? Resolve(Dictionary<string, XElement> types, string typeName) =>
            types.TryGetValue(typeName, out var exact)
                ? exact
                : types.Values.FirstOrDefault(t =>
                    (string?)t.Attribute("Name") == typeName[(typeName.LastIndexOf('.') + 1)..]);

        static IReadOnlyList<ODataProperty> Properties(Dictionary<string, XElement> types, string typeName)
        {
            if (Resolve(types, typeName) is not { } type) return [];

            // Base type first, so inherited properties keep their position at the front.
            var inherited = (string?)type.Attribute("BaseType") is { } baseType ? Properties(types, baseType) : [];

            var keys = type.Elements().Where(e => e.Name.LocalName == "Key")
                .SelectMany(k => k.Elements().Where(e => e.Name.LocalName == "PropertyRef"))
                .Select(p => (string?)p.Attribute("Name"))
                .ToHashSet();

            var own = type.Elements().Where(e => e.Name.LocalName == "Property").Select(p =>
            {
                var name = (string?)p.Attribute("Name") ?? "";
                // CSDL defaults Nullable to true; a key is never nullable whatever it says.
                return new ODataProperty(name, (string?)p.Attribute("Type") ?? "Edm.String",
                    !keys.Contains(name) && (string?)p.Attribute("Nullable") != "false", keys.Contains(name));
            });

            return inherited.Concat(own).ToList();
        }
    }
}

public sealed class ODataSession(ConnectionSpec spec, HttpClient http, Uri root, ODataMetadata metadata) : IDbSession
{
    public ConnectionSpec Spec { get; } = spec;
    public HttpClient Http { get; } = http;
    public Uri Root { get; } = root;
    public ODataMetadata Metadata { get; } = metadata;

    /// Nothing here is ADO.NET. Anything reaching for Connection is a bug, not a fallback.
    public DbConnection Connection =>
        throw new NotSupportedException("OData does not expose an ADO.NET connection");

    public ValueTask DisposeAsync()
    {
        Http.Dispose();
        return ValueTask.CompletedTask;
    }
}

/// Reads an OData service (V2 to V4) over HTTP. The connection string is the service root URL;
/// `user:pw@` in it is sent as Basic authentication, `bearer:<token>@` as a Bearer token.
public sealed class ODataDriver(HttpMessageHandler? handler = null) : IDbDriver
{
    private const int BatchSize = 200;

    public DriverInfo Info { get; } = new("odata", "OData", 443, "https://host/service.svc/");

    public DriverCapabilities Caps { get; } = new() { Sql = false, TabularBrowse = false };

    public SqlDialect Dialect { get; } = new ODataDialect();

    public async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var url = new Uri(spec.ConnectionString, UriKind.Absolute);
        if (url.Scheme is not ("http" or "https"))
            throw new FormatException("an OData connection string is the http(s) URL of the service root");

        // The root always ends in a slash so relative resource paths append instead of replacing
        // the last segment; the credentials leave the URL and travel as a header.
        var root = new UriBuilder(url) { UserName = "", Password = "" };
        if (!root.Path.EndsWith('/')) root.Path += "/";

        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Authorization = Authorization(url.UserInfo);

        try
        {
            var csdl = await http.GetStringAsync(new Uri(root.Uri, "$metadata"), ct);
            return new ODataSession(spec, http, root.Uri, ODataMetadata.Parse(csdl));
        }
        catch (Exception)
        {
            http.Dispose();
            throw;
        }
    }

    private static AuthenticationHeaderValue? Authorization(string userInfo)
    {
        if (userInfo.Length == 0) return null;

        var parts = userInfo.Split(':', 2).Select(Uri.UnescapeDataString).ToArray();
        if (parts.Length == 2 && parts[0].Equals("bearer", StringComparison.OrdinalIgnoreCase))
            return new AuthenticationHeaderValue("Bearer", parts[1]);

        return new AuthenticationHeaderValue("Basic",
            Convert.ToBase64String(Encoding.UTF8.GetBytes(string.Join(':', parts))));
    }

    public Task<IReadOnlyList<SchemaNode>> IntrospectAsync(IDbSession session, SchemaNodeRef? parent,
        CancellationToken ct)
    {
        if (parent is not null) return Task.FromResult<IReadOnlyList<SchemaNode>>([]);

        // An entity set is the closest thing to a table; the UI treats it as one.
        var sets = Cast(session).Metadata.EntitySets.Keys
            .Order(StringComparer.OrdinalIgnoreCase)
            .Select(name => new SchemaNode(new SchemaNodeRef(SchemaNodeKind.Table, [name]), name, false))
            .ToList();
        return Task.FromResult<IReadOnlyList<SchemaNode>>(sets);
    }

    public async Task<ObjectDetail> DescribeAsync(IDbSession session, SchemaNodeRef target, CancellationToken ct)
    {
        var odata = Cast(session);
        if (!odata.Metadata.EntitySets.TryGetValue(target.Name, out var properties))
            throw new KeyNotFoundException($"the service has no entity set '{target.Name}'");

        var columns = properties
            .Select((p, i) => new ColumnInfo(p.Name, p.Type, p.Nullable, null, p.IsKey, false, null, i + 1))
            .ToList();

        return new ObjectDetail(target, columns, [], [], [], await CountAsync(odata, target.Name, ct),
            null, null, null);
    }

    /// Not every service implements $count; a missing count is shown as unknown, not as an error.
    private static async Task<long?> CountAsync(ODataSession odata, string set, CancellationToken ct)
    {
        try
        {
            var text = await odata.Http.GetStringAsync(new Uri(odata.Root, $"{set}/$count"), ct);
            return long.TryParse(text.Trim(), out var count) ? count : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    public async IAsyncEnumerable<ResultChunk> ExecuteAsync(IDbSession session, ScriptRequest request,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var odata = Cast(session);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        var limit = request.MaxRows > 0 ? request.MaxRows : int.MaxValue;

        // Line breaks are ignored so a long filter can be wrapped in the editor.
        var query = string.Join(" ", request.Sql.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0));
        if (query.Length == 0)
        {
            yield return new ResultChunk.End(0, 0, watch.ElapsedMilliseconds, false);
            yield break;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        if (request.TimeoutSeconds > 0) timeout.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));

        var total = 0;
        var truncated = false;
        Uri? next = new Uri(odata.Root, query);

        while (next is not null && total < limit)
        {
            var (page, nextLink, error) = await FetchAsync(odata, next, timeout.Token);
            if (error is not null)
            {
                yield return error;
                yield break;
            }

            if (page.Count > limit - total)
            {
                page = page.Take(limit - total).ToList();
                truncated = true;
            }

            for (var offset = 0; offset < page.Count; offset += BatchSize)
            {
                var batch = page.Skip(offset).Take(BatchSize).ToList();
                total += batch.Count;
                yield return new ResultChunk.Documents(0, batch);
                yield return new ResultChunk.Progress(0, total, watch.ElapsedMilliseconds);
            }

            // A next link with nothing left to give is still a next link; the limit decides.
            next = nextLink is null ? null : new Uri(odata.Root, nextLink);
            if (next is not null && total >= limit) truncated = true;
        }

        yield return new ResultChunk.End(0, 0, watch.ElapsedMilliseconds, truncated);
    }

    private static async Task<(List<JsonElement> Items, string? NextLink, ResultChunk.Error? Error)> FetchAsync(
        ODataSession odata, Uri url, CancellationToken ct)
    {
        HttpResponseMessage response;
        string body;
        try
        {
            response = await odata.Http.GetAsync(url, ct);
            body = await response.Content.ReadAsStringAsync(ct);
        }
        catch (HttpRequestException e)
        {
            return ([], null, new ResultChunk.Error(0, e.Message, null, null, null));
        }
        catch (TaskCanceledException) when (!ct.IsCancellationRequested)
        {
            return ([], null, new ResultChunk.Error(0, "the service did not answer within the timeout", null, null, null));
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
                return ([], null, new ResultChunk.Error(0, ErrorText(body, response.ReasonPhrase),
                    ((int)response.StatusCode).ToString(), null, null));

            // $count and $value answer with a bare scalar rather than a JSON document.
            if (response.Content.Headers.ContentType?.MediaType?.Contains("json") != true)
                return ([Scalar(body.Trim())], null, null);

            try
            {
                using var document = JsonDocument.Parse(body);
                return Unwrap(document.RootElement);
            }
            catch (JsonException e)
            {
                return ([], null, new ResultChunk.Error(0, $"the service did not answer with JSON: {e.Message}",
                    null, null, null));
            }
        }
    }

    private static JsonElement Scalar(string text)
    {
        var value = long.TryParse(text, out var number)
            ? number.ToString()
            : JsonSerializer.Serialize(text);
        return JsonDocument.Parse($"{{\"count\":{value}}}").RootElement.Clone();
    }

    /// V4 and V3 put the entities in `value`; V2 wraps them in `d.results` (or `d` alone for a
    /// single entity). Anything else is one entity by itself.
    private static (List<JsonElement>, string?, ResultChunk.Error?) Unwrap(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Array) return (root.EnumerateArray().Select(Entity).ToList(), null, null);
        if (root.ValueKind != JsonValueKind.Object) return ([root.Clone()], null, null);

        if (root.TryGetProperty("d", out var d))
        {
            if (d.ValueKind == JsonValueKind.Object && d.TryGetProperty("results", out var results))
                return (results.EnumerateArray().Select(Entity).ToList(),
                    d.TryGetProperty("__next", out var v2Next) ? v2Next.GetString() : null, null);
            return Unwrap(d);
        }

        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            var next = root.TryGetProperty("@odata.nextLink", out var v4) ? v4.GetString()
                : root.TryGetProperty("odata.nextLink", out var v3) ? v3.GetString()
                : null;
            return (value.EnumerateArray().Select(Entity).ToList(), next, null);
        }

        return ([Entity(root)], null, null);
    }

    /// Control information (`@odata.etag`, `@odata.id`, V2's `__metadata`) would otherwise become a
    /// column in every result; the entity's own properties are what the user asked for.
    private static JsonElement Entity(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return element.Clone();

        var properties = element.EnumerateObject()
            .Where(p => !p.Name.StartsWith('@') && !p.Name.StartsWith("odata.") && p.Name != "__metadata")
            .ToList();
        if (properties.Count == element.EnumerateObject().Count()) return element.Clone();

        using var buffer = new System.IO.MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var property in properties) property.WriteTo(writer);
            writer.WriteEndObject();
        }

        return JsonDocument.Parse(buffer.ToArray()).RootElement.Clone();
    }

    /// OData error bodies nest the message two or three levels deep; fall back to the raw body.
    private static string ErrorText(string body, string? reason)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            var error = document.RootElement.TryGetProperty("error", out var e) ? e : document.RootElement;
            if (error.TryGetProperty("message", out var message))
            {
                var text = message.ValueKind == JsonValueKind.Object && message.TryGetProperty("value", out var v2)
                    ? v2.GetString()
                    : message.GetString();
                if (text is { Length: > 0 }) return text;
            }
        }
        catch (JsonException)
        {
        }

        return body.Length > 0 ? body[..Math.Min(body.Length, 500)] : reason ?? "the request failed";
    }

    public Task<PlanNode> ExplainAsync(IDbSession session, string sql, PlanMode mode, CancellationToken ct) =>
        throw new NotSupportedException("OData has no query planner");

    public Task<AnalyzeReport> AnalyzeAsync(IDbSession session, AnalyzeScope scope, SchemaNodeRef? target,
        CancellationToken ct) => Task.FromResult(new AnalyzeReport([]));

    private static ODataSession Cast(IDbSession session) =>
        // Unwrap: a pooled or tunnelled session is a wrapper around the one this driver opened.
        session.Unwrap() as ODataSession
        ?? throw new InvalidOperationException("this session does not belong to the OData driver");
}
