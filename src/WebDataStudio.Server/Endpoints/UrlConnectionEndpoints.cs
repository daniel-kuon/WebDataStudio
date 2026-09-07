using WebDataStudio.Server.Services;

namespace WebDataStudio.Server.Endpoints;

/// What the studio's own URL asked for.
///
/// A POST rather than something the browser does on its own: the parameter arrives at the server
/// once, the answer says per entry what happened, and the browser then drops `u` from the address
/// bar so a screenshot of the tab is not a copy of the credentials.
public static class UrlConnectionEndpoints
{
    /// The `?u=` parameter, whole and unsplit — commas inside it belong to the entries.
    public sealed record FromUrlRequest(string? U);

    public static void MapUrlConnectionEndpoints(this WebApplication app)
    {
        app.MapPost("/api/connections/from-url", async (FromUrlRequest body, HttpContext ctx,
            UrlConnectionOpener opener, CancellationToken ct) =>
        {
            var entries = UrlEntry.ParseAll(body.U);

            // Nothing in the parameter is not an error: the studio just opens normally.
            if (entries.Count == 0) return Results.Ok(new { opened = Array.Empty<OpenedConnection>() });

            var opened = await opener.OpenAsync(entries, SessionConnections.Key(ctx), ct);

            return Results.Ok(new { opened });
        });
    }
}
