using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using WebDataStudio.Server.Models;

namespace WebDataStudio.Server.Services;

/// Connections that belong to one browser and to this process.
///
/// A studio handed out as a viewer usually has no accounts at all, so the owner cannot be the
/// signed-in user — `CurrentUser.User` is null in anonymous mode. The key is a cookie this class
/// sets instead. Nothing here is written to disk: a password that came in through a URL does not
/// outlive the process, which is the point of the default.
///
/// A studio that is open for weeks also needs its sessions to end, so each key carries when it was
/// last seen and `Sweep` drops the quiet ones along with the files they brought.
public sealed class SessionConnections(
    IHttpContextAccessor accessor, StudioAccess access, FileRoots roots,
    ILogger<SessionConnections>? log = null)
{
    public const string CookieName = "wds_session";

    private sealed record Session(
        ConcurrentDictionary<string, ConnectionSpec> Connections, DateTimeOffset Seen)
    {
        public DateTimeOffset Seen { get; set; } = Seen;
    }

    private readonly ConcurrentDictionary<string, Session> _byKey = new(StringComparer.Ordinal);

    /// Every key that has connections or has been seen. For tests and for a status page; not for
    /// deciding anything.
    public IEnumerable<string> Keys => _byKey.Keys;

    /// The key for the request in flight, setting the cookie where the browser has none.
    public static string Key(HttpContext context)
    {
        if (context.Items.TryGetValue(CookieName, out var pending) && pending is string already)
            return already;

        if (context.Request.Cookies.TryGetValue(CookieName, out var existing)
            && existing is { Length: > 0 })
            return existing;

        var fresh = Guid.NewGuid().ToString("n");

        context.Response.Cookies.Append(CookieName, fresh, Cookie(context));

        // The cookie is on its way out, so this request already knows its own key.
        context.Items[CookieName] = fresh;
        return fresh;
    }

    /// How the cookie is written, in one place, because the delete has to match the set exactly or
    /// the browser keeps it.
    public static CookieOptions Cookie(HttpContext context) => new()
    {
        HttpOnly = true,
        IsEssential = true,
        SameSite = SameSiteMode.Lax,
        Secure = context.Request.IsHttps,
    };

    /// Keeps one connection for one browser. The same id twice is the same connection, so a link
    /// opened again does not pile up — and a connection made in the form arrives without an id,
    /// which is where it gets one.
    ///
    /// Throws past the ceiling: one browser holding a thousand connections on a studio anybody can
    /// reach is a leak with a friendly face.
    public ConnectionSpec Add(string key, ConnectionSpec spec)
    {
        var session = spec with
        {
            Id = spec.Id is { Length: > 0 } given ? given : Guid.NewGuid().ToString("n"),
            Source = ConnectionSource.Session,
        };

        var held = _byKey.GetOrAdd(key, _ => new Session(new(StringComparer.Ordinal), Now));

        // Replacing one of your own is not adding another one.
        if (!held.Connections.ContainsKey(session.Id)
            && held.Connections.Count >= access.MaxSessionConnections)
            throw new InvalidOperationException(
                $"this studio holds at most {access.MaxSessionConnections} connections per browser "
                + "(WDS_SESSION_MAX_CONNECTIONS); close one before opening another");

        held.Connections[session.Id] = session;
        return session;
    }

    public IReadOnlyList<ConnectionSpec> For(string key) =>
        _byKey.TryGetValue(key, out var found) ? found.Connections.Values.ToList() : [];

    /// What the request in flight may see. No context — a background service, a test that has no
    /// request — means none, which is the safe answer rather than everybody's.
    public IReadOnlyList<ConnectionSpec> Current
    {
        get
        {
            if (accessor.HttpContext is not { } context) return [];

            var key = context.Items.TryGetValue(CookieName, out var pending) && pending is string fresh
                ? fresh
                : context.Request.Cookies.TryGetValue(CookieName, out var cookie) ? cookie : null;

            return key is { Length: > 0 } ? For(key) : [];
        }
    }

    /// Drops one connection from one browser. False when it was not there, which the caller
    /// answers as a 404: a connection somebody else owns does not exist for you.
    public bool Remove(string key, string id) =>
        _byKey.TryGetValue(key, out var found) && found.Connections.TryRemove(id, out _);

    /// Everything this browser brought, gone now: the connections and the files behind them. What
    /// the **Forget my connections** button does.
    public void Forget(string key)
    {
        _byKey.TryRemove(key, out _);
        DeleteFiles(key);
    }

    /// Remembers a key even before it has connections, so `Keys` says who is here, and stamps it as
    /// seen — which is what keeps a session somebody is still using alive.
    public void Remember(string key) => Remember(key, Now);

    /// The same, with the time said out loud. Tests use it to age a session; nothing else should.
    public void Remember(string key, DateTimeOffset seen)
    {
        var held = _byKey.GetOrAdd(key, _ => new Session(new(StringComparer.Ordinal), seen));
        held.Seen = seen;
    }

    /// Drops every session that has gone quiet for longer than the lifetime allows, and the files
    /// each of them brought. Returns how many went.
    ///
    /// No lifetime means nothing is swept: a studio one person runs on their own machine should not
    /// lose a connection because they went to lunch.
    public int Sweep(DateTimeOffset now)
    {
        if (access.SessionTtl is not { } ttl) return 0;

        var gone = 0;

        foreach (var (key, session) in _byKey.ToArray())
        {
            if (now - session.Seen < ttl) continue;

            _byKey.TryRemove(key, out _);
            DeleteFiles(key);
            gone++;
        }

        if (gone > 0) log?.LogInformation("swept {Count} session(s) that had gone quiet", gone);

        return gone;
    }

    /// A folder name for one session's files. The key itself is a cookie value and has no business
    /// being a path on disk.
    public static string FolderFor(string key) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)))[..16];

    /// Whether a path is inside the tree where sessions keep their uploads. A `?u=` file entry is
    /// refused there: the folder names are GUIDs and not guessable, but a path that leaked would
    /// otherwise hand one visitor another visitor's database.
    public bool IsSomebodysUpload(string path)
    {
        var tree = Path.Combine(roots.Uploads, "session");

        return Path.GetFullPath(path)
            .StartsWith(Path.GetFullPath(tree) + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private void DeleteFiles(string key)
    {
        var folder = Path.Combine(roots.Uploads, "session", FolderFor(key));

        try
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
        }
        catch (IOException e)
        {
            // A file the studio cannot remove is worth a line, not a crash in a background sweep.
            log?.LogWarning(e, "could not remove the files of a session that ended");
        }
    }

    private static DateTimeOffset Now => DateTimeOffset.UtcNow;
}

/// Ends the sessions nobody came back to.
///
/// Every five minutes rather than on a timer per session: a sweep is a walk over a dictionary with
/// as many entries as there are visitors, and a studio with visitors has other things to do.
public sealed class SessionSweeper(SessionConnections sessions, StudioAccess access) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (access.SessionTtl is null) return;

        using var clock = new PeriodicTimer(TimeSpan.FromMinutes(5));

        try
        {
            while (await clock.WaitForNextTickAsync(stoppingToken))
                sessions.Sweep(DateTimeOffset.UtcNow);
        }
        catch (OperationCanceledException)
        {
            // Shutting down between two sweeps is not a failure.
        }
    }
}
