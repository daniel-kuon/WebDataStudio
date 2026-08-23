using WebDataStudio.Server.Ddl;
using WebDataStudio.Server.Drivers;
using WebDataStudio.Server.Drivers.Abstractions;
using WebDataStudio.Server.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Caching.Memory;

namespace WebDataStudio.Server.Endpoints;

public static class SchemaEndpoints
{
    public static void MapSchemaEndpoints(this WebApplication app)
    {
        // What moved since the last snapshot, for anybody who wants to know why a query stopped
        // working. Absent unless WDS_SCHEMA_SNAPSHOT_DIR is set: without it there is nothing to
        // compare against.
        app.MapGet("/api/schema/{conn}/drift", (string conn, SchemaSnapshots snapshots) =>
            !snapshots.Configured
                ? Results.Ok(new { configured = false, drift = (object?)null })
                : Results.Ok(new
                {
                    configured = true,
                    drift = snapshots.DriftOf(conn) is { } drift
                        ? new
                        {
                            before = drift.Before,
                            after = drift.After,
                            summary = drift.Summary,
                            drift.Added,
                            drift.Removed,
                            drift.Changed,
                        }
                        : null,
                }));

        // Takes one now rather than waiting for the next start — the button behind "did my
        // migration do what I think it did".
        app.MapPost("/api/schema/snapshot", async (SchemaSnapshots snapshots, CancellationToken ct) =>
            snapshots.Configured
                ? Results.Ok(new { moved = await snapshots.SweepAsync(ct) })
                : Results.BadRequest(new
                {
                    message = "no snapshot directory is configured; set WDS_SCHEMA_SNAPSHOT_DIR",
                }));

        app.MapGet("/api/drivers", (DriverRegistry drivers) =>
            Results.Ok(drivers.All().Select(d => new { d.Info, d.Caps })));

        app.MapGet("/api/schema/{conn}", async (string conn, string? parent,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var parentRef = string.IsNullOrEmpty(parent) ? null : SchemaNodeRef.Parse(parent);
                    var nodes = await driver.IntrospectAsync(session, parentRef, ct);

                    // Every driver stops at the object itself. Its columns, indexes, keys and
                    // triggers are already in DescribeAsync, so the tree grows one level deeper
                    // here rather than in nine drivers.
                    if (nodes.Count == 0 && parentRef is { Kind: SchemaNodeKind.Table
                            or SchemaNodeKind.View or SchemaNodeKind.MaterializedView })
                        nodes = ObjectChildren(parentRef, await driver.DescribeAsync(session, parentRef, ct));
                    return Results.Ok(nodes.Select(n => new
                    {
                        @ref = n.Ref.ToString(),
                        kind = n.Ref.Kind.ToString(),
                        label = n.Label,
                        hasChildren = n.HasChildren,
                        detail = n.Detail,
                    }));
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // The object reference travels in the query string, not the path: it contains a slash
        // ("Table:dbo/AbpUsers"), and the reverse proxy in front of a deployed studio — Envoy on
        // Azure Container Apps, and most others — decodes %2F back to a real slash before routing.
        // The route then no longer matches and every object lookup answered 404 in the cloud while
        // working on a machine with nothing in front of it.
        app.MapGet("/api/schema/{conn}/object", async (string conn, [FromQuery(Name = "ref")] string objectRef,
            SessionFactory factory, CancellationToken ct) =>
        {
            try
            {
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                    return Results.Ok(await driver.DescribeAsync(session, ParseObjectRef(objectRef), ct));
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });

        // The other direction of DescribeAsync's foreign keys: who points at this table. An object
        // only knows its own keys, so the whole schema is read once — cached briefly, the way the
        // diagram already reads it — and every table's keys are checked against the target.
        app.MapGet("/api/schema/{conn}/referencing", async (string conn,
            [FromQuery(Name = "ref")] string objectRef, SessionFactory factory, IMemoryCache cache,
            CancellationToken ct) =>
        {
            try
            {
                var target = ParseObjectRef(objectRef);
                var (driver, session) = await factory.OpenAsync(conn, ct);
                await using (session)
                {
                    var tables = await cache.GetOrCreateAsync($"referencing:{conn}", async entry =>
                    {
                        entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60);
                        return await CompareEndpoints.ReadSchemaAsync(driver, session, null, ct);
                    }) ?? [];

                    var targetSchema = target.Path.Count > 1 ? target.Path[0] : "";
                    var keys = tables.SelectMany(table => table.Constraints
                        .Where(constraint => constraint.Kind == ConstraintKind.ForeignKey
                            && constraint.ReferencedTable is not null
                            && References(constraint.ReferencedTable, table.Schema, targetSchema, target.Name))
                        .Select(constraint => new
                        {
                            name = constraint.Name,
                            schema = table.Schema,
                            table = table.Name,
                            tableRef = table.Schema is { Length: > 0 }
                                ? $"Table:{table.Schema}/{table.Name}"
                                : $"Table:{table.Name}",
                            columns = constraint.Columns,
                            referencedColumns = constraint.ReferencedColumns ?? [],
                        }));

                    return Results.Ok(keys);
                }
            }
            catch (UnknownConnectionException e) { return Results.NotFound(new { message = e.Message }); }
            catch (FormatException e) { return Results.BadRequest(new { message = e.Message }); }
            catch (Exception e) { return Results.Json(new { message = e.Message }, statusCode: 502); }
        });
    }

    /// Whether a foreign key's referenced table is the asked-for one. A referenced table without a
    /// schema means "the owner's schema"; a schema is only compared when both sides have one, so an
    /// engine that qualifies and one that does not still agree about the same table.
    private static bool References(string referenced, string ownerSchema, string targetSchema, string targetName)
    {
        var dot = referenced.IndexOf('.');
        var schema = dot > 0 ? referenced[..dot] : ownerSchema;
        var name = dot > 0 ? referenced[(dot + 1)..] : referenced;

        if (!name.Equals(targetName, StringComparison.OrdinalIgnoreCase)) return false;
        return schema.Length == 0 || targetSchema.Length == 0
            || schema.Equals(targetSchema, StringComparison.OrdinalIgnoreCase);
    }

    /// Routing decodes every percent-escape in a route value except %2F, which stays encoded so a
    /// slash cannot silently split a segment. Object references contain slashes, so put them back —
    /// and only them, since decoding the whole value again would corrupt a name containing a literal
    /// percent sign.
    /// The parts of an object as tree nodes: columns first, then indexes, foreign keys and
    /// triggers. The reference of each carries its parent's path, so an action knows the table.
    private static IReadOnlyList<SchemaNode> ObjectChildren(SchemaNodeRef parent, ObjectDetail detail)
    {
        SchemaNodeRef Child(SchemaNodeKind kind, string name) =>
            new(kind, [.. parent.Path, name]);

        var nodes = new List<SchemaNode>();

        nodes.AddRange(detail.Columns.OrderBy(c => c.Position).Select(column => new SchemaNode(
            Child(SchemaNodeKind.Column, column.Name),
            column.Name, false,
            $"{column.DataType}{(column.Nullable ? "" : " not null")}{(column.IsPrimaryKey ? " · pk" : "")}")));

        nodes.AddRange(detail.Indexes.Select(index => new SchemaNode(
            Child(SchemaNodeKind.Index, index.Name),
            index.Name, false,
            string.Join(", ", index.Columns)
            + (index.Primary ? " · primary" : index.Unique ? " · unique" : "")
            + (index.FullText ? " · full text" : ""))));

        nodes.AddRange(detail.ForeignKeys.Select(key => new SchemaNode(
            Child(SchemaNodeKind.ForeignKey, key.Name),
            key.Name, false,
            $"{string.Join(", ", key.Columns)} → {key.ReferencedTable}")));

        nodes.AddRange(detail.Triggers.Select(trigger => new SchemaNode(
            Child(SchemaNodeKind.Trigger, trigger.Name),
            trigger.Name, false, $"{trigger.Timing} {trigger.Event}")));

        return nodes;
    }

    internal static SchemaNodeRef ParseObjectRef(string value) =>
        SchemaNodeRef.Parse(value.Replace("%2F", "/", StringComparison.OrdinalIgnoreCase));
}
