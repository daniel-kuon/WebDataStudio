using System.Security.Cryptography;
using System.Text;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// What came of one `?u=` entry: a connection, or the sentence saying why not.
public sealed record OpenedConnection(string? Id, string Label, string? Refused);

/// Turns what the studio's own URL named into connections.
///
/// Every refusal names the setting that would allow it, because the person reading it is usually the
/// person who can change it. And one refused entry does not take the others with it: a link with
/// three databases in it opens the two it may.
public sealed class UrlConnectionOpener(
    UrlConnectionOptions options,
    FileRoots roots,
    SessionConnections sessions,
    ConnectionStore store,
    IHttpClientFactory clients)
{
    /// The named client, so a deployment can put a proxy or a certificate in front of it.
    public const string HttpClientName = "url-connections";

    public async Task<IReadOnlyList<OpenedConnection>> OpenAsync(
        IReadOnlyList<UrlEntry> entries, string sessionKey, CancellationToken ct)
    {
        var opened = new List<OpenedConnection>();

        foreach (var entry in entries)
            opened.Add(await OneAsync(entry, sessionKey, ct));

        return opened;
    }

    private async Task<OpenedConnection> OneAsync(UrlEntry entry, string sessionKey, CancellationToken ct)
    {
        if (!options.Enabled)
            return Refused(entry, "this studio does not open connections from its URL; a deployment "
                                  + "allows it with WDS_OPEN_FROM_URL");

        if (!options.Allows(entry.Kind))
            return Refused(entry, entry.Kind switch
            {
                UrlEntryKind.File => "WDS_OPEN_FROM_URL does not allow 'file' on this studio",
                UrlEntryKind.Download => "WDS_OPEN_FROM_URL does not allow 'download' on this studio",
                _ => "WDS_OPEN_FROM_URL does not allow 'connection-string' on this studio — it has to "
                     + "be named, because a connection string in a URL is a password in browser history",
            });

        return entry.Kind switch
        {
            UrlEntryKind.File => FromFile(entry, entry.Value, sessionKey),
            UrlEntryKind.Download => await FromDownloadAsync(entry, sessionKey, ct),
            _ => FromConnectionString(entry, sessionKey),
        };
    }

    private OpenedConnection FromFile(UrlEntry entry, string path, string sessionKey)
    {
        if (roots.Resolve(path) is not { } resolved || !File.Exists(resolved))
            return Refused(entry, $"'{path}' is not a file this studio may read; a deployment names "
                                  + "the folders it may in WDS_FILE_ROOTS");

        if (FileConnections.Refusal(resolved) is { } refusal) return Refused(entry, refusal);

        // A file a visitor uploaded is not something a link may name. The folder names are GUIDs
        // and not guessable, but a path that leaked would otherwise open somebody else's database.
        // What this opener fetches itself lives under `<uploads>/url/`, outside that tree.
        if (sessions.IsSomebodysUpload(resolved))
            return Refused(entry, $"'{Path.GetFileName(resolved)}' belongs to somebody else's "
                                  + "session; a link cannot name a file another visitor brought");

        var kind = FileConnections.KindOf(resolved);

        if (kind == FileConnectionKind.Unsupported)
            return Refused(entry, $"'{Path.GetExtension(resolved)}' is not a database this studio opens");

        if (kind == FileConnectionKind.Sqlite)
        {
            try
            {
                if (!FileConnections.LooksLikeSqlite(resolved))
                    return Refused(entry, "this file is not a SQLite database — it does not start "
                                          + "with 'SQLite format 3'");
            }
            catch (IOException e)
            {
                // A file locked by something else is a sentence, not a 500 on a link somebody clicked.
                return Refused(entry, $"'{Path.GetFileName(resolved)}' cannot be read: {e.Message}");
            }
        }

        return Keep(entry, sessionKey, FileConnections.EngineOf(kind),
            FileConnections.ConnectionStringFor(kind, resolved),
            // A data file is read-only whatever the switch says: a view over a CSV is not something
            // to write through.
            FileConnections.ReadOnlyByNature(kind) || !options.Writable,
            Path.GetFileName(resolved));
    }

    private OpenedConnection FromConnectionString(UrlEntry entry, string sessionKey)
    {
        // A label that is an engine name is how a connection string says which engine it is for,
        // and it is asked first: the guess is for the strings that say so themselves.
        var engine = entry.Label is { Length: > 0 } label
                     && ConnectionRegistry.KnownEngines.Contains(label, StringComparer.OrdinalIgnoreCase)
            ? label.ToLowerInvariant()
            : FileConnections.EngineFromConnectionString(entry.Value);

        if (engine is null)
            return Refused(entry, "this connection string does not say which engine it is for; name "
                                  + "it as the label, for example postgres:Host=…");

        return Keep(entry, sessionKey, engine, entry.Value, !options.Writable,
            entry.Label ?? engine.ToUpperInvariant());
    }

    private async Task<OpenedConnection> FromDownloadAsync(
        UrlEntry entry, string sessionKey, CancellationToken ct)
    {
        if (!Uri.TryCreate(entry.Value, UriKind.Absolute, out var uri))
            return Refused(entry, $"'{entry.Value}' is not a URL this studio can fetch");

        if (!options.HostAllowed(uri.Host))
            return Refused(entry, options.Hosts.Count == 0
                ? "a download needs the hosts it may fetch from in WDS_OPEN_FROM_URL_HOSTS; without "
                  + "them the studio would fetch whatever a link says"
                : $"{uri.Host} is not in WDS_OPEN_FROM_URL_HOSTS");

        var name = Path.GetFileName(uri.LocalPath) is { Length: > 0 } file ? file : "download.db";

        if (FileConnections.Refusal(name) is { } refusal) return Refused(entry, refusal);

        if (FileConnections.KindOf(name) == FileConnectionKind.Unsupported)
            return Refused(entry, $"'{Path.GetExtension(name)}' is not a database this studio opens");

        var directory = Path.Combine(roots.Uploads, "url", Fingerprint(entry.Value));
        var path = Path.Combine(directory, name);

        // Fetched once. The same link again is the same connection, not a second download.
        if (!File.Exists(path))
        {
            var problem = await FetchAsync(uri, directory, path, ct);
            if (problem is not null) return Refused(entry, problem);
        }

        return FromFile(entry, path, sessionKey);
    }

    private async Task<string?> FetchAsync(Uri uri, string directory, string path, CancellationToken ct)
    {
        using var client = clients.CreateClient(HttpClientName);

        try
        {
            using var response = await client.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!response.IsSuccessStatusCode)
                return $"{uri} answered {(int)response.StatusCode}";

            if (response.Content.Headers.ContentLength is { } declared && declared > options.MaxBytes)
                return "the file is larger than WDS_OPEN_FROM_URL_MAX_MB allows";

            Directory.CreateDirectory(directory);

            await using (var target = File.Create(path))
            await using (var source = await response.Content.ReadAsStreamAsync(ct))
            {
                // Counted while copying, because a server that declares no length can still send
                // more than the disk has.
                var buffer = new byte[80 * 1024];
                long written = 0;

                while (await source.ReadAsync(buffer, ct) is var read && read > 0)
                {
                    written += read;

                    if (written > options.MaxBytes)
                    {
                        target.Close();
                        Delete(directory);
                        return "the file is larger than WDS_OPEN_FROM_URL_MAX_MB allows";
                    }

                    await target.WriteAsync(buffer.AsMemory(0, read), ct);
                }
            }

            return null;
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Delete(directory);
            return $"{uri} could not be fetched: {e.Message}";
        }
    }

    private OpenedConnection Keep(UrlEntry entry, string sessionKey, string engine,
        string connectionString, bool readOnly, string fallbackName)
    {
        var name = entry.Label is { Length: > 0 } label ? label : fallbackName;

        var spec = new ConnectionSpec(entry.Id, name, engine, connectionString, readOnly,
            null, null, ConnectionSource.Session);

        if (!options.KeepInStore)
        {
            sessions.Add(sessionKey, spec);
            return new OpenedConnection(spec.Id, name, null);
        }

        // The other branch: written down like any other connection, visible to everybody. What a
        // single-user build wants and what a shared deployment must not have — hence the default.
        if (store.Get(entry.Id) is not null) return new OpenedConnection(entry.Id, name, null);

        try
        {
            var stored = store.Add(spec with { Source = ConnectionSource.Stored });
            return new OpenedConnection(stored.Id, stored.Name, null);
        }
        catch (InvalidOperationException e)
        {
            return new OpenedConnection(null, name, e.Message);
        }
    }

    private static OpenedConnection Refused(UrlEntry entry, string why) =>
        new(null, entry.Label ?? Shorten(entry), why);

    /// What to call an entry in a message when it has no label — and never the whole thing, because
    /// a connection string is not something to echo back.
    private static string Shorten(UrlEntry entry) => entry.Kind == UrlEntryKind.ConnectionString
        ? "the connection string"
        : Path.GetFileName(entry.Value) is { Length: > 0 } file ? file : entry.Value;

    private static void Delete(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, true); }
        catch (IOException) { /* a file the studio cannot remove is not worth failing the request for */ }
    }

    private static string Fingerprint(string text) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(text)))[..16];
}
