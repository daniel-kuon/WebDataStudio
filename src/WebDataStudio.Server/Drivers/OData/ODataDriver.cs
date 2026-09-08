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

/// One member of an entity: a plain property, or a navigation property — the relation `$expand`
/// follows. `Type` is the CSDL type as written, so `Collection(Shop.Order)` says both what it
/// points at and that there are many of them.
public sealed record ODataProperty(string Name, string Type, bool Nullable, bool IsKey,
    bool IsNavigation = false)
{
    public bool IsCollection => Type.StartsWith("Collection(", StringComparison.Ordinal);
}

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

            // The relations, kept rather than skipped: they are what the query builder offers under
            // $expand, and an entity without them can only ever be read one table at a time.
            var navigations = type.Elements().Where(e => e.Name.LocalName == "NavigationProperty")
                .Select(p => new ODataProperty((string?)p.Attribute("Name") ?? "",
                    (string?)p.Attribute("Type") ?? "", true, false, IsNavigation: true));

            return inherited.Concat(own).Concat(navigations).ToList();
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
/// `user:pw@` in it is sent as Basic authentication, `bearer:<token>@` as a Bearer token. Any
/// further line is a request header, `Name: value` — the way to hand over a session cookie or an
/// API key a service wants under its own name.
public sealed class ODataDriver(HttpMessageHandler? handler = null) : IDbDriver
{
    private const int BatchSize = 200;

    public DriverInfo Info { get; } = new("odata", "OData", 443, "https://host/service.svc/");

    public DriverCapabilities Caps { get; } = new() { Sql = false };

    public SqlDialect Dialect { get; } = new ODataDialect();

    public async Task<IDbSession> OpenAsync(ConnectionSpec spec, CancellationToken ct)
    {
        var lines = spec.ConnectionString.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();
        if (lines.Length == 0) throw new FormatException("an OData connection string is the http(s) URL of the service root");
        var url = new Uri(lines[0], UriKind.Absolute);
        if (url.Scheme is not ("http" or "https"))
            throw new FormatException("an OData connection string is the http(s) URL of the service root");

        // The root always ends in a slash so relative resource paths append instead of replacing
        // the last segment; the credentials leave the URL and travel as a header.
        var root = new UriBuilder(url) { UserName = "", Password = "" };
        if (!root.Path.EndsWith('/')) root.Path += "/";

        var http = handler is null ? new HttpClient() : new HttpClient(handler, disposeHandler: false);
        http.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        http.DefaultRequestHeaders.Authorization = Authorization(url.UserInfo);
        foreach (var line in lines.Skip(1))
        {
            var split = line.Split(':', 2);
            if (split.Length != 2) throw new FormatException($"'{line}' is not a header; write it as Name: value");
            http.DefaultRequestHeaders.TryAddWithoutValidation(split[0].Trim(), split[1].Trim());
        }

        try
        {
            // $metadata is XML; a service that honours Accept strictly answers 406 to "json".
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(root.Uri, "$metadata"));
            request.Headers.Accept.ParseAdd("application/xml");
            using var response = await http.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            var csdl = await response.Content.ReadAsStringAsync(ct);
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
        CancellationToken ct, bool systemObjects = false)
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

        // A navigation property is part of the entity, so it is listed — with what it points at as
        // its type and a comment saying it is followed rather than read.
        var columns = properties
            .Select((p, i) => new ColumnInfo(p.Name, p.Type, p.Nullable, null, p.IsKey, false,
                p.IsNavigation ? "navigation property: expand it to read it" : null, i + 1))
            .ToList();

        return new ObjectDetail(target, columns, [], [], [], await CountAsync(odata, target.Name, null, ct),
            null, null, null);
    }

    /// Not every service implements $count; a missing count is shown as unknown, not as an error.
    private static async Task<long?> CountAsync(ODataSession odata, string set, string? filter, CancellationToken ct)
    {
        try
        {
            var query = filter is null ? "" : $"?$filter={Uri.EscapeDataString(filter)}";
            var text = await odata.Http.GetStringAsync(new Uri(odata.Root, $"{set}/$count{query}"), ct);
            return long.TryParse(text.Trim(), out var count) ? count : null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            return null;
        }
    }

    /// The data tab's page, built as OData query options so the service does the paging, sorting
    /// and filtering rather than the studio pulling the whole set.
    public async Task<TabularPage?> PageAsync(IDbSession session, SchemaNodeRef target, PageQuery query,
        CancellationToken ct)
    {
        if (target.Kind != SchemaNodeKind.Table) return null;

        var odata = Cast(session);
        if (!odata.Metadata.EntitySets.TryGetValue(target.Name, out var properties))
            throw new KeyNotFoundException($"the service has no entity set '{target.Name}'");

        // What the query builder put together, if it was used. The grid owns the paging either way.
        var built = ODataOptions.Parse(query.Options);
        var options = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["$top"] = query.Limit.ToString(),
            ["$skip"] = query.Offset.ToString(),
        };

        foreach (var (name, value) in built)
            if (name is not ("$top" or "$skip" or "$count")) options[name] = value;

        string? note = null;

        // A column header's own sort wins over the builder's: it is the later of the two gestures.
        var sortColumn = properties.FirstOrDefault(p =>
            !p.IsNavigation && p.Name.Equals(query.Sort, StringComparison.OrdinalIgnoreCase));
        if (sortColumn is not null) options["$orderby"] = $"{sortColumn.Name}{(query.Desc ? " desc" : "")}";

        var filterColumn = properties.FirstOrDefault(p =>
            !p.IsNavigation && p.Name.Equals(query.FilterColumn, StringComparison.OrdinalIgnoreCase));
        if (filterColumn is not null && query.Filter is { Length: > 0 })
        {
            var (column, columnNote) = ODataFilter.Build(filterColumn, query.Filter);
            note = columnNote;

            // Both filters hold: the builder's, and the one typed into this column's header.
            if (column is not null)
                options["$filter"] = options.TryGetValue("$filter", out var existing) && existing.Length > 0
                    ? $"({existing}) and ({column})"
                    : column;
        }

        options.TryGetValue("$filter", out var filter);

        var url = new Uri(odata.Root,
            $"{target.Name}?{string.Join('&', options.Select(o => $"{o.Key}={Uri.EscapeDataString(o.Value)}"))}");
        var (items, _, error) = await FetchAsync(odata, url, ct);
        if (error is not null) throw new InvalidOperationException(error.Text);

        // $select narrows the columns to the ones asked for; $expand adds one per relation, holding
        // the JSON the service sent back, which the grid's own viewer opens as a tree.
        var selected = Names(options, "$select");
        var expanded = Names(options, "$expand").Select(e => e.Split('(', 2)[0].Trim()).ToList();

        var shown = properties
            .Where(p => p.IsNavigation
                ? expanded.Contains(p.Name, StringComparer.OrdinalIgnoreCase)
                : selected.Count == 0 || selected.Contains(p.Name, StringComparer.OrdinalIgnoreCase))
            .ToList();

        var columns = shown.Select(p => new ColumnMeta(p.Name, p.Type, p.Nullable)).ToList();
        var rows = items.Select(item => shown.Select(p =>
            item.ValueKind == JsonValueKind.Object && item.TryGetProperty(p.Name, out var value)
                ? Value(value)
                : null).ToArray()).ToList();

        return new TabularPage(columns, rows, await CountAsync(odata, target.Name, filter, ct),
            Editable: false, Reason: "an OData connection only reads", Note: note);

        // A comma-separated option, split into its names. `$expand=Orders($top=5),Category` keeps
        // its parentheses together, because a nested option may hold a comma of its own.
        static List<string> Names(Dictionary<string, string> options, string name)
        {
            if (!options.TryGetValue(name, out var value) || value.Length == 0) return [];

            var found = new List<string>();
            var depth = 0;
            var start = 0;

            for (var i = 0; i < value.Length; i++)
            {
                if (value[i] == '(') depth++;
                else if (value[i] == ')') depth--;
                else if (value[i] == ',' && depth == 0)
                {
                    found.Add(value[start..i].Trim());
                    start = i + 1;
                }
            }

            found.Add(value[start..].Trim());
            return found.Where(f => f.Length > 0).ToList();
        }
    }

    private static object? Value(JsonElement element) => element.ValueKind switch
    {
        JsonValueKind.Null or JsonValueKind.Undefined => null,
        JsonValueKind.String => element.GetString(),
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.Number => element.TryGetInt64(out var whole) ? (object)whole : element.GetDouble(),
        _ => element.GetRawText(),
    };

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

/// The query options of a request, read back apart. What the query builder sends is exactly what a
/// person would type into the address bar, so it arrives either raw or percent-encoded.
public static class ODataOptions
{
    public static IReadOnlyList<(string Name, string Value)> Parse(string? query)
    {
        if (query is null) return [];

        var text = query.Trim().TrimStart('?');
        var found = new List<(string, string)>();

        foreach (var part in text.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var split = part.Split('=', 2);
            if (split.Length != 2) continue;

            var name = split[0].Trim();
            if (!name.StartsWith('$')) continue;

            // A value that came through a URL is decoded once; one typed by hand has nothing to
            // decode, and Unescape leaves it alone.
            found.Add((name, Uri.UnescapeDataString(split[1]).Trim()));
        }

        return found;
    }
}

/// The grid's column filter language, said in OData. One term only: `a b` (and) and `a,b` (or)
/// are noted rather than half-translated.
public static class ODataFilter
{
    public static (string? Filter, string? Note) Build(ODataProperty column, string expression)
    {
        var text = expression.Trim();
        if (text.Length == 0) return (null, null);

        var name = column.Name;
        var isText = column.Type == "Edm.String";
        string Literal(string raw) => isText ? "'" + raw.Replace("'", "''") + "'" : raw.Trim();

        if (text.Equals("NULL", StringComparison.OrdinalIgnoreCase)) return ($"{name} eq null", null);
        if (text.Equals("!NULL", StringComparison.OrdinalIgnoreCase)
            || text.Equals("NOT NULL", StringComparison.OrdinalIgnoreCase)) return ($"{name} ne null", null);

        // A list of alternatives is what ticking values in the column menu writes.
        if (text.StartsWith('=') && text.Contains(",=", StringComparison.Ordinal))
            return (string.Join(" or ", text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(part => $"{name} eq {Literal(part.TrimStart('='))}")), null);

        if (text.Contains(',') || !isText && text.Contains(' '))
            return (null, "an OData filter here takes one term: no AND (space) or OR (comma)");

        if (text.StartsWith(">=")) return ($"{name} ge {Literal(text[2..])}", null);
        if (text.StartsWith("<=")) return ($"{name} le {Literal(text[2..])}", null);
        if (text.StartsWith("!=")) return ($"{name} ne {Literal(text[2..])}", null);
        if (text.StartsWith('>')) return ($"{name} gt {Literal(text[1..])}", null);
        if (text.StartsWith('<')) return ($"{name} lt {Literal(text[1..])}", null);
        if (text.StartsWith('=')) return ($"{name} eq {Literal(text[1..])}", null);
        if (text.StartsWith('~'))
            return isText
                ? ($"not contains({name},{Literal(text[1..])})", null)
                : ($"{name} ne {Literal(text[1..])}", null);

        if (!isText) return ($"{name} eq {Literal(text)}", null);
        if (text.StartsWith('^')) return ($"startswith({name},{Literal(text[1..])})", null);
        if (text.StartsWith('$')) return ($"endswith({name},{Literal(text[1..])})", null);
        return ($"contains({name},{Literal(text)})", null);
    }
}
